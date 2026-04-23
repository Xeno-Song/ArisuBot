using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using Microsoft.Extensions.Options;

namespace ArisuBot.Core.Services;

/// <summary>대화 컨텍스트 조회, 메시지 목록 구성, 메시지 저장을 담당한다. LLM 호출은 포함하지 않는다.</summary>
public class ConversationService
{
    private readonly IConversationRepository _repository;
    private readonly MemoryOptions _options;

    public ConversationService(IConversationRepository repository, IOptions<MemoryOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    /// <summary>타입에 따라 유저 또는 채널 컨텍스트를 반환한다.</summary>
    public Task<ConversationContext> GetContextAsync(ulong targetId, ContextType type, CancellationToken ct = default)
    {
        return type switch
        {
            ContextType.User    => _repository.GetUserContextAsync(targetId, ct),
            ContextType.Channel => _repository.GetChannelContextAsync(targetId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// 시스템 프롬프트 + 히스토리(최대 N개, Role.System 제외) + 새 유저 메시지를 조합해 LLM 입력 목록을 반환한다.
    /// Role.System은 TakeLast로 잘릴 수 있어 항상 fresh로 prepend한다.
    /// DB에 저장된 System 메시지는 감사 목적이며 LLM 입력에서는 중복 방지를 위해 제외한다.
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildMessageList(
        ConversationContext context, string newUserMessage, string systemPrompt)
    {
        var maxHistory = context.Type == ContextType.User
            ? _options.UserContextMaxMessages
            : _options.ChannelContextMaxMessages;

        // Role.System은 TakeLast 기준에서 제외 — 항상 첫 번째로 fresh prepend
        var history = context.Messages
            .Where(m => m.Role != Role.System)
            .TakeLast(maxHistory);

        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = systemPrompt }
        };
        messages.AddRange(history);
        messages.Add(new() { Role = Role.User, Content = newUserMessage });

        return messages;
    }

    /// <summary>컨텍스트에 메시지를 추가하고 저장소에 반영한다.</summary>
    public async Task AppendMessageAsync(
        ConversationContext context, ChatMessage message, CancellationToken ct = default)
    {
        context.Messages.Add(message);
        context.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveContextAsync(context, ct);
    }

    /// <summary>LLM 요청 토큰 사용량을 컨텍스트에 추가하고 저장소에 반영한다.</summary>
    public async Task AppendTokenUsageAsync(
        ConversationContext context, TokenUsage usage, CancellationToken ct = default)
    {
        context.TokenUsage.Add(usage);
        context.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveContextAsync(context, ct);
    }

    /// <summary>기존 세션을 보존하고 새 빈 세션을 생성한다. /new-session 커맨드에서 사용.</summary>
    public Task<ConversationContext> StartNewSessionAsync(
        ulong targetId, ContextType type, CancellationToken ct = default)
        => _repository.CreateNewSessionAsync(targetId, type, ct);
}
