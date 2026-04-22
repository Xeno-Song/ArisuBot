using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>대화 컨텍스트 영속성 추상화.</summary>
public interface IConversationRepository
{
    Task<ConversationContext> GetUserContextAsync(ulong userId, CancellationToken ct = default);
    Task<ConversationContext> GetChannelContextAsync(ulong channelId, CancellationToken ct = default);
    Task SaveContextAsync(ConversationContext context, CancellationToken ct = default);
    Task ClearContextAsync(ConversationContext context, CancellationToken ct = default);
}
