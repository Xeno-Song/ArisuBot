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
    /// <summary>모든 대화 도큐먼트의 dynamicCacheRef를 제거하고 캐시 관련 카운터를 초기화한다. 프로세스 시작 시 stale 캐시 참조 제거에 사용.</summary>
    Task ClearAllDynamicCacheRefsAsync(CancellationToken ct = default);
    /// <summary>MongoDB ObjectId 문자열로 특정 세션을 조회한다. 없으면 null 반환.</summary>
    Task<ConversationContext?> GetContextByIdAsync(string id, CancellationToken ct = default);
    /// <summary>type/targetId 기준으로 모든 세션을 생성 시각 오름차순으로 반환한다. 세션 목록 조회 및 BackgroundService Compaction에 사용.</summary>
    Task<List<ConversationContext>> GetAllSessionsAsync(ContextType type, ulong targetId, CancellationToken ct = default);
    /// <summary>Compaction 트리거 평가 대상 활성 세션 전체를 반환한다. BackgroundService에서 사용.</summary>
    Task<List<ConversationContext>> GetAllActiveContextsAsync(CancellationToken ct = default);
}
