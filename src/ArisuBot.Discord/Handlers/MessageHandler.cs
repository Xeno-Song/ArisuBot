using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using ArisuBot.Discord.Options;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ArisuBot.Discord.Handlers;

/// <summary>Discord 메시지 이벤트를 수신하고 LLM 응답을 오케스트레이션한다.</summary>
public class MessageHandler
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordOptions _discordOptions;
    private readonly MessageListenerOptions _listenerOptions;
    private readonly ConversationService _conversationService;
    private readonly ILLMProvider _llmProvider;
    private readonly ILLMCacheManager _cacheManager;
    private readonly IReadOnlyList<ILLMTool> _tools;
    private readonly IAIMessageLogger _messageLogger;
    private readonly IAdminNotifier _adminNotifier;
    private readonly IPromptLoader _promptLoader;
    private readonly ICompactionService _compactionService;
    private readonly CompactionTriggerEvaluator _triggerEvaluator;
    private readonly ILogger<MessageHandler> _logger;

    // Session Resume 등으로 인한 동일 메시지 중복 처리 방지
    private readonly ConcurrentDictionary<ulong, DateTime> _processedMessages = new();
    private static readonly TimeSpan MessageTtl = TimeSpan.FromMinutes(5);

    // LLM 생성 중 수신된 메시지를 컨텍스트별로 수집 — 완료 후 일괄 처리 (Coalescing)
    private readonly ConcurrentDictionary<string, ContextSlot> _slots = new();

    [ExcludeFromCodeCoverage(Justification = "DiscordSocketClient 실연결 의존 — DI 배선 전용, 비즈니스 로직 없음.")]
    public MessageHandler(
        DiscordSocketClient client,
        IOptions<DiscordOptions> options,
        ConversationService conversationService,
        ILLMProvider llmProvider,
        ILLMCacheManager cacheManager,
        IEnumerable<ILLMTool> tools,
        IAIMessageLogger messageLogger,
        IAdminNotifier adminNotifier,
        IPromptLoader promptLoader,
        ICompactionService compactionService,
        CompactionTriggerEvaluator triggerEvaluator,
        ILogger<MessageHandler> logger)
    {
        _client              = client;
        _discordOptions      = options.Value;
        _listenerOptions     = options.Value.MessageListener;
        _conversationService = conversationService;
        _llmProvider         = llmProvider;
        _cacheManager        = cacheManager;
        _tools               = tools.ToList();
        _messageLogger       = messageLogger;
        _adminNotifier       = adminNotifier;
        _promptLoader        = promptLoader;
        _compactionService   = compactionService;
        _triggerEvaluator    = triggerEvaluator;
        _logger              = logger;
    }

    /// <summary>메시지 수신 시 호출. 응답 여부 판단 후 컨텍스트 슬롯에 등록 — LLM 처리 중이면 pending 큐에 누적.</summary>
    [ExcludeFromCodeCoverage(Justification = "Discord.Net sealed 구체 타입(SocketUserMessage) 의존. E2E 테스트 대상.")]
    public async Task HandleAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage userMessage) return;

        // 동일 message.Id 중복 처리 방지 (Session Resume으로 인한 재전달 대응)
        var now = DateTime.UtcNow;
        if (!_processedMessages.TryAdd(userMessage.Id, now)) return;
        // TTL 초과 항목 정리 — 무한 메모리 증가 방지
        foreach (var entry in _processedMessages.Where(e => now - e.Value > MessageTtl).ToList())
            _processedMessages.TryRemove(entry.Key, out _);

        var isMention = userMessage.MentionedUsers.Any(u => u.Id == _client.CurrentUser?.Id);

        ContextType contextType;
        ulong targetId;
        ulong guildId = 0;

        if (userMessage.Channel is SocketDMChannel)
        {
            // DM: AdminUserIds ∪ DmWhitelistExtraUserIds에 포함된 유저만 응답
            var whitelist = _discordOptions.AdminUserIds.Union(_discordOptions.DmWhitelistExtraUserIds);
            if (!whitelist.Contains(userMessage.Author.Id)) return;

            contextType = ContextType.User;
            targetId    = userMessage.Author.Id;
        }
        else if (userMessage.Channel is SocketGuildChannel guildChannel)
        {
            if (!ShouldRespond(_listenerOptions, userMessage.Channel.Id, isMention)) return;

            contextType = ContextType.Channel;
            targetId    = userMessage.Channel.Id;
            guildId     = guildChannel.Guild.Id;
        }
        else
        {
            return;
        }

        _logger.LogDebug("메시지 수신 처리 — messageId={MessageId} userId={UserId} channelId={ChannelId} contextType={ContextType}",
            userMessage.Id, userMessage.Author.Id, userMessage.Channel.Id, contextType);

        var displayName = GetDisplayName(userMessage.Author);
        var pending     = new PendingMessage(userMessage, displayName, guildId, contextType, targetId, isMention);
        var slotKey     = $"{(int)contextType}:{targetId}";
        var slot        = _slots.GetOrAdd(slotKey, _ => new ContextSlot());

        lock (slot)
        {
            if (slot.IsProcessing)
            {
                // LLM 처리 중 — 완료 후 일괄 처리를 위해 pending 큐에 누적
                slot.Pending.Add(pending);
                _logger.LogDebug("LLM 처리 중 — 메시지 pending 추가 slotKey={SlotKey} pendingCount={Count}",
                    slotKey, slot.PendingCount);
                return;
            }
            slot.IsProcessing = true;
        }

        // HandleAsync는 즉시 반환 — 실제 LLM 처리는 백그라운드 루프에서 진행
        _ = Task.Run(() => RunProcessingLoopAsync(slot, slotKey, pending, contextType, targetId, guildId));

        // HandleAsync가 Task를 반환하므로 await 없이 fire-and-forget. 워닝 억제를 위해 명시적 완료.
        await Task.CompletedTask;
    }

    /// <summary>컨텍스트 슬롯 처리 루프. pending 메시지 전체 드레인 후 배치 처리 반복.</summary>
    [ExcludeFromCodeCoverage(Justification = "Discord.Net sealed 구체 타입 의존. E2E 테스트 대상.")]
    private async Task RunProcessingLoopAsync(
        ContextSlot slot,
        string slotKey,
        PendingMessage first,
        ContextType contextType,
        ulong targetId,
        ulong guildId)
    {
        using var typingCts = new CancellationTokenSource();
        // typing indicator를 루프 전체 기간 동안 유지 — 배치 전환 시에도 끊기지 않게
        _ = KeepTypingAsync(first.Message.Channel, typingCts.Token);

        var batch = new List<PendingMessage> { first };

        try
        {
            while (true)
            {
                try
                {
                    await ProcessBatchAsync(batch, contextType, targetId, guildId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // ProcessBatchAsync 내부에서 사용자 오류 메시지 전송 완료 — 루프 안전망 로그
                    _logger.LogError(ex, "배치 처리 예외 — slotKey={SlotKey}", slotKey);
                }

                List<PendingMessage> next;
                lock (slot)
                {
                    if (slot.Pending.Count == 0)
                    {
                        // 처리할 메시지 없음 — 루프 정상 종료
                        slot.IsProcessing = false;
                        return;
                    }
                    // 처리 완료 시점까지 쌓인 메시지 전체 드레인 — 1회 LLM 호출로 일괄 처리
                    next = new List<PendingMessage>(slot.Pending);
                    slot.Pending.Clear();
                }

                _logger.LogDebug("pending 메시지 일괄 처리 시작 — slotKey={SlotKey} count={Count}", slotKey, next.Count);
                batch = next;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("처리 루프 취소 — slotKey={SlotKey}", slotKey);
            lock (slot) { slot.IsProcessing = false; }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "처리 루프 비정상 종료 — slotKey={SlotKey}", slotKey);
            lock (slot) { slot.IsProcessing = false; }
        }
        finally
        {
            typingCts.Cancel();
        }
    }

    /// <summary>배치 메시지를 단일 LLM 호출로 처리하고 Discord 응답을 전송한다.</summary>
    [ExcludeFromCodeCoverage(Justification = "Discord.Net sealed 구체 타입 의존. E2E 테스트 대상.")]
    private async Task ProcessBatchAsync(
        IReadOnlyList<PendingMessage> batch,
        ContextType contextType,
        ulong targetId,
        ulong guildId)
    {
        var firstMsg = batch[0].Message;

        try
        {
            var context = await _conversationService.GetContextAsync(targetId, contextType);

            // 컨텍스트 최초 생성 시 system → persona 순서로 DB에 1회 저장
            if (context.Messages.Count == 0)
            {
                await _conversationService.AppendMessageAsync(context,
                    new ChatMessage { Role = Role.System, Content = _promptLoader.SystemPrompt });
                await _conversationService.AppendMessageAsync(context,
                    new ChatMessage { Role = Role.User, Content = _promptLoader.PersonaPrompt });
            }

            // 배치 내 모든 발신자 Participants 등록 — <<name>> mention 치환에 사용
            foreach (var p in batch)
                context.Participants[p.DisplayName] = p.Message.Author.Id;

            // 배치 메시지 결합 — 채널: [이름]: 내용 형식, DM: 내용 단순 \n 연결
            var batchItems         = batch.Select(p => (p.Message.Content, p.DisplayName)).ToList();
            var combinedUserContent = CombineBatchContent(batchItems, contextType);

            // 단일 메시지면 발신자 이름 유지, 복수이면 내용에 포함됐으므로 null
            var senderName = batch.Count == 1 ? batch[0].DisplayName : null;
            var newMessage = new ChatMessage
            {
                Role       = Role.User,
                Content    = combinedUserContent,
                SenderName = senderName
            };

            // BuildMessageList: 현재 컨텍스트 히스토리 + 새 유저 메시지 조합
            var messages = _conversationService.BuildMessageList(
                context, newMessage, _promptLoader.SystemPrompt);

            // velocity 추적용 타임스탬프 기록
            context.RecentMessageTimestamps.Add(DateTimeOffset.UtcNow);

            // 캐시 롤 시도 — velocity + threshold 조건 충족 시 새 캐시 생성/교체
            var cacheHint = await _cacheManager.TryRollCacheAsync(context, messages, _tools);

            // 유저 메시지 DB 저장 (갱신된 DynamicCacheRef 포함)
            await _conversationService.AppendMessageAsync(context, newMessage);

            // Guild 채널에서만 툴 활성화 — DM은 서버 컨텍스트가 없으므로 툴 비활성화
            var toolContext = guildId != 0
                ? new LLMToolExecutionContext(guildId, firstMsg.Channel.Id)
                : null;

            _logger.LogInformation(
                "LLM 호출 시작 — channelId={ChannelId} batchSize={BatchSize} toolsEnabled={ToolsEnabled} messageCount={MessageCount} cacheMode={CacheMode}",
                firstMsg.Channel.Id, batch.Count, toolContext is not null, messages.Count, cacheHint is not null);

            // 멘션이 있는 첫 번째 메시지 기준 reply — 없으면 일반 메시지
            var mentionMsg = batch.FirstOrDefault(p => p.IsMention);
            MessageReference? reference = mentionMsg is not null
                ? new MessageReference(mentionMsg.Message.Id)
                : null;

            var responses = new List<LLMResponse>();

            // 응답이 완성된 순서대로 즉시 Discord 전송 — 툴 실행 중간 텍스트도 지체 없이 전달
            await foreach (var response in _llmProvider.GenerateAsync(messages, _tools, toolContext, cacheHint, context.Id))
            {
                responses.Add(response);

                // 각 응답의 Tool 이력 즉시 저장 — AssistantMessage 앞 순서 보장
                foreach (var toolMsg in response.ToolCallHistory)
                    await _conversationService.AppendMessageAsync(context, toolMsg);

                // Discord 즉시 전송 — <<name>>을 <@userId>로 치환
                foreach (var chunk in SplitIntoChunks(ReplaceMentions(response.Content, context.Participants)))
                    await firstMsg.Channel.SendMessageAsync(chunk, messageReference: reference);
            }

            var totalTokensIn  = responses.Sum(r => r.TokensIn);
            var totalTokensOut = responses.Sum(r => r.TokensOut);
            var totalCached    = responses.Sum(r => r.TokensCachedIn);

            _logger.LogInformation(
                "LLM 호출 완료 — tokensIn={TokensIn} tokensOut={TokensOut} tokensCached={TokensCached} responseLength={ResponseLength}",
                totalTokensIn, totalTokensOut, totalCached, responses.Sum(r => r.Content.Length));

            // 전체 응답 합산 — DB 저장 및 로그용
            var combinedContent = string.Join("\n", responses.Select(r => r.Content));
            var providerName    = responses.FirstOrDefault()?.ProviderName ?? _llmProvider.ProviderName;

            // DB에는 LLM 원본 출력(<<name>> 형태) 저장
            await _conversationService.AppendMessageAsync(context,
                new ChatMessage
                {
                    Role                 = Role.Assistant,
                    Content              = combinedContent,
                    ProviderMetadataJson = responses.LastOrDefault()?.ProviderMetadataJson
                });

            // 응답 후 비캐시 토큰 누적 — 다음 요청의 캐시 롤 임계값 판단에 사용
            context.UncachedTokenCount += totalTokensIn - totalCached;
            // 마지막 총 토큰 수 갱신 — 재시작 후 UncachedTokenCount 복원 기준값
            context.LastTotalTokens = totalTokensIn + totalTokensOut;

            // 복수 응답의 토큰 합산 후 컨텍스트에 저장
            await _conversationService.AppendTokenUsageAsync(context, new TokenUsage
            {
                TokensIn       = totalTokensIn,
                TokensOut      = totalTokensOut,
                TokensCachedIn = totalCached
            });

            // 로그에는 Discord mention 형태(<@userId>)로 치환된 내용 저장
            var processedContent = ReplaceMentions(combinedContent, context.Participants);
            await _messageLogger.LogAsync(
                guildId, targetId, firstMsg.Author.Id,
                combinedUserContent, processedContent, providerName);

            // Compaction 트리거 평가 — token 기반 트리거만 인라인 처리 (inactivity/scheduled는 BackgroundService)
            if (_triggerEvaluator.IsTokenThresholdMet(context) &&
                _triggerEvaluator.ShouldCompact(context))
            {
                // 비동기 fire-and-forget — 메시지 응답 지연 없이 백그라운드에서 실행
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _compactionService.RunAsync(context, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Compaction 실행 실패 — contextId={Id}", context.Id);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "메시지 처리 실패 — channelId={ChannelId} userId={UserId}",
                firstMsg.Channel.Id, firstMsg.Author.Id);

            await _adminNotifier.NotifyAsync(
                $"[ArisuBot 오류] {ex.GetType().Name}: {ex.Message}" +
                $"\n채널: {firstMsg.Channel.Id}\n유저: {firstMsg.Author.Id}");

            // LLM 전체 실패 시 사용자에게 채널 메시지 전송 (reply 아님)
            await firstMsg.Channel.SendMessageAsync(
                "지금은 응답하기 어렵습니다. 잠시 후 다시 말 걸어주세요!");
        }
    }

    /// <summary>
    /// 배치 메시지를 LLM 입력용 단일 문자열로 결합한다.
    /// 채널: [이름]: 내용\n[이름]: 내용, DM: 내용\n내용 (항상 동일 발신자).
    /// </summary>
    internal static string CombineBatchContent(
        IReadOnlyList<(string Content, string DisplayName)> batch,
        ContextType contextType)
    {
        if (batch.Count == 1) return batch[0].Content;

        // DM은 항상 동일 유저이므로 이름 접두사 불필요
        return contextType == ContextType.User
            ? string.Join("\n", batch.Select(p => p.Content))
            : string.Join("\n", batch.Select(p => $"[{p.DisplayName}]: {p.Content}"));
    }

    /// <summary>채널 typing 인디케이터를 8초 간격으로 유지한다. Discord typing 표시 10초 TTL 대응.</summary>
    [ExcludeFromCodeCoverage(Justification = "IMessageChannel.TriggerTypingAsync Discord.Net 의존 — E2E 테스트 대상.")]
    private static async Task KeepTypingAsync(IMessageChannel channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await channel.TriggerTypingAsync();
                await Task.Delay(8000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Discord 유저에서 표시 이름을 추출한다. 서버 닉네임 > 글로벌 이름 > 사용자명 우선순위.</summary>
    [ExcludeFromCodeCoverage(Justification = "SocketGuildUser/SocketUser는 Discord.Net sealed 구체 타입 — E2E 테스트 대상.")]
    internal static string GetDisplayName(SocketUser author)
    {
        if (author is SocketGuildUser guildUser)
            return guildUser.DisplayName;
        return author.GlobalName ?? author.Username;
    }

    /// <summary>텍스트 내 &lt;&lt;name&gt;&gt; 패턴을 Discord mention &lt;@userId&gt;로 치환한다. Participants에 없는 name은 원본 유지.</summary>
    internal static string ReplaceMentions(string text, IReadOnlyDictionary<string, ulong> participants)
        => Regex.Replace(text, @"<<([^>]+)>>", match =>
        {
            var name = match.Groups[1].Value;
            return participants.TryGetValue(name, out var userId) ? $"<@{userId}>" : match.Value;
        });

    /// <summary>채널 목록 포함 여부 또는 멘션 설정에 따라 응답 여부를 반환한다.</summary>
    internal static bool ShouldRespond(MessageListenerOptions options, ulong channelId, bool isMention)
    {
        if (options.ChannelIds.Contains(channelId)) return true;
        if (isMention && options.RespondToMentions) return true;
        return false;
    }

    /// <summary>문자열을 maxLength 단위로 분할한다. Discord 2000자 제한 대응.</summary>
    internal static IEnumerable<string> SplitIntoChunks(string text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
    }

    /// <summary>라우팅 완료 후 처리 대기 중인 메시지 정보.</summary>
    internal sealed record PendingMessage(
        SocketUserMessage Message,
        string DisplayName,
        ulong GuildId,
        ContextType ContextType,
        ulong TargetId,
        bool IsMention);

    /// <summary>컨텍스트별 처리 상태 슬롯. lock 보호 하에 접근.</summary>
    internal sealed class ContextSlot
    {
        /// <summary>현재 LLM 처리 진행 중 여부.</summary>
        public bool IsProcessing { get; set; }

        /// <summary>처리 완료 후 일괄 처리할 대기 메시지 목록.</summary>
        internal List<PendingMessage> Pending { get; } = new();

        /// <summary>대기 중인 메시지 수. 테스트 접근용.</summary>
        internal int PendingCount => Pending.Count;
    }
}
