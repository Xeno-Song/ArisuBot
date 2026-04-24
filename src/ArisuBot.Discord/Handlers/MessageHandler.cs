using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
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
    private readonly IReadOnlyList<ILLMTool> _tools;
    private readonly IAIMessageLogger _messageLogger;
    private readonly IAdminNotifier _adminNotifier;
    private readonly IPromptLoader _promptLoader;
    private readonly ILogger<MessageHandler> _logger;

    // Session Resume 등으로 인한 동일 메시지 중복 처리 방지
    private readonly ConcurrentDictionary<ulong, DateTime> _processedMessages = new();
    private static readonly TimeSpan MessageTtl = TimeSpan.FromMinutes(5);

    [ExcludeFromCodeCoverage(Justification = "DiscordSocketClient 실연결 의존 — DI 배선 전용, 비즈니스 로직 없음.")]
    public MessageHandler(
        DiscordSocketClient client,
        IOptions<DiscordOptions> options,
        ConversationService conversationService,
        ILLMProvider llmProvider,
        IEnumerable<ILLMTool> tools,
        IAIMessageLogger messageLogger,
        IAdminNotifier adminNotifier,
        IPromptLoader promptLoader,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _discordOptions = options.Value;
        _listenerOptions = options.Value.MessageListener;
        _conversationService = conversationService;
        _llmProvider = llmProvider;
        _tools = tools.ToList();
        _messageLogger = messageLogger;
        _adminNotifier = adminNotifier;
        _promptLoader = promptLoader;
        _logger = logger;
    }

    /// <summary>메시지 수신 시 호출. 응답 여부 판단 후 LLM 호출 및 응답 전송.</summary>
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
            targetId = userMessage.Author.Id;
        }
        else if (userMessage.Channel is SocketGuildChannel guildChannel)
        {
            if (!ShouldRespond(_listenerOptions, userMessage.Channel.Id, isMention)) return;

            contextType = ContextType.Channel;
            targetId = userMessage.Channel.Id;
            guildId = guildChannel.Guild.Id;
        }
        else
        {
            return;
        }

        _logger.LogDebug("메시지 수신 처리 — messageId={MessageId} userId={UserId} channelId={ChannelId} contextType={ContextType}",
            userMessage.Id, userMessage.Author.Id, userMessage.Channel.Id, contextType);

        await userMessage.Channel.TriggerTypingAsync();

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

            // 발신자 표시 이름 추출 후 Participants 업데이트 (<<name>> mention 치환에 사용)
            var displayName = GetDisplayName(userMessage.Author);
            context.Participants[displayName] = userMessage.Author.Id;

            var newMessage = new ChatMessage { Role = Role.User, Content = userMessage.Content, SenderName = displayName };

            // BuildMessageList: 현재 컨텍스트 히스토리 + 새 유저 메시지 조합
            var messages = _conversationService.BuildMessageList(
                context, newMessage, _promptLoader.SystemPrompt);

            // 유저 메시지를 DB에 저장 (BuildMessageList 호출 후 저장으로 중복 방지, Participants도 함께 저장됨)
            await _conversationService.AppendMessageAsync(context, newMessage);

            // Guild 채널에서만 툴 활성화 — DM은 서버 컨텍스트가 없으므로 툴 비활성화
            var toolContext = guildId != 0
                ? new LLMToolExecutionContext(guildId, userMessage.Channel.Id)
                : null;

            _logger.LogInformation(
                "LLM 호출 시작 — channelId={ChannelId} userId={UserId} toolsEnabled={ToolsEnabled} messageCount={MessageCount}",
                userMessage.Channel.Id, userMessage.Author.Id, toolContext is not null, messages.Count);

            // 멘션 응답이면 reply, 일반이면 일반 메시지
            MessageReference? reference = isMention ? new MessageReference(userMessage.Id) : null;
            var responses = new List<LLMResponse>();

            // 응답이 완성된 순서대로 즉시 Discord 전송 — 툴 실행 중간 텍스트도 지체 없이 전달
            await foreach (var response in _llmProvider.GenerateAsync(messages, _tools, toolContext))
            {
                responses.Add(response);

                // 각 응답의 Tool 이력 즉시 저장 — AssistantMessage 앞 순서 보장
                foreach (var toolMsg in response.ToolCallHistory)
                    await _conversationService.AppendMessageAsync(context, toolMsg);

                // Discord 즉시 전송 — <<name>>을 <@userId>로 치환
                foreach (var chunk in SplitIntoChunks(ReplaceMentions(response.Content, context.Participants)))
                    await userMessage.Channel.SendMessageAsync(chunk, messageReference: reference);
            }

            _logger.LogInformation(
                "LLM 호출 완료 — tokensIn={TokensIn} tokensOut={TokensOut} responseLength={ResponseLength}",
                responses.Sum(r => r.TokensIn), responses.Sum(r => r.TokensOut),
                responses.Sum(r => r.Content.Length));

            // 전체 응답 합산 — DB 저장 및 로그용
            var combinedContent = string.Join("\n", responses.Select(r => r.Content));
            var providerName = responses.FirstOrDefault()?.ProviderName ?? _llmProvider.ProviderName;

            // DB에는 LLM 원본 출력(<<name>> 형태) 저장 — 히스토리에서 LLM이 동일 패턴 유지하도록
            // ProviderMetadataJson(thought_signature 등): 마지막 응답에서 추출 — BuildContents에서 model Content로 복원해 cache hit 유지
            await _conversationService.AppendMessageAsync(context,
                new ChatMessage
                {
                    Role                 = Role.Assistant,
                    Content              = combinedContent,
                    ProviderMetadataJson = responses.LastOrDefault()?.ProviderMetadataJson
                });

            // 복수 응답의 토큰 합산 후 컨텍스트에 저장
            await _conversationService.AppendTokenUsageAsync(context, new TokenUsage
            {
                TokensIn       = responses.Sum(r => r.TokensIn),
                TokensOut      = responses.Sum(r => r.TokensOut),
                TokensCachedIn = responses.Sum(r => r.TokensCachedIn)
            });

            // 로그에는 Discord mention 형태(<@userId>)로 치환된 내용 저장
            var processedContent = ReplaceMentions(combinedContent, context.Participants);
            await _messageLogger.LogAsync(
                guildId, targetId, userMessage.Author.Id,
                userMessage.Content, processedContent, providerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "메시지 처리 실패 — channelId={ChannelId} userId={UserId}",
                userMessage.Channel.Id, userMessage.Author.Id);

            await _adminNotifier.NotifyAsync(
                $"[ArisuBot 오류] {ex.GetType().Name}: {ex.Message}" +
                $"\n채널: {userMessage.Channel.Id}\n유저: {userMessage.Author.Id}");
        }
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
}
