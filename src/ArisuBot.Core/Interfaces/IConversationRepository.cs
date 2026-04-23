using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>대화 컨텍스트 영속성 추상화.</summary>
public interface IConversationRepository
{
    Task<ConversationContext> GetUserContextAsync(ulong userId, CancellationToken ct = default);
    Task<ConversationContext> GetChannelContextAsync(ulong channelId, CancellationToken ct = default);
    Task SaveContextAsync(ConversationContext context, CancellationToken ct = default);
    /// <summary>기존 세션을 보존하고 새 빈 컨텍스트 도큐먼트를 삽입한다.</summary>
    Task<ConversationContext> CreateNewSessionAsync(ulong targetId, ContextType type, CancellationToken ct = default);
}
