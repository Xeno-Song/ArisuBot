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

    /// <summary>시스템 프롬프트 + 히스토리(최대 N개) + 새 유저 메시지를 조합해 LLM 입력 목록을 반환한다.</summary>
    public IReadOnlyList<ChatMessage> BuildMessageList(
        ConversationContext context, string newUserMessage, string systemPrompt)
    {
        var maxHistory = context.Type == ContextType.User
            ? _options.UserContextMaxMessages
            : _options.ChannelContextMaxMessages;

        var history = context.Messages.TakeLast(maxHistory);

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
}
