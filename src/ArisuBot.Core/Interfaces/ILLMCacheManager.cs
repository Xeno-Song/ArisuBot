using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>
/// LLM 명시적 캐시 생명주기 추상화.
/// ArisuBot.Discord가 ArisuBot.LLM을 직접 참조하지 않도록 Core에 위치한다.
/// </summary>
public interface ILLMCacheManager
{
    /// <summary>
    /// velocity + threshold 조건을 검사하고 필요 시 캐시를 생성/교체한다.
    /// 기존 캐시가 유효하고 임계값 미달이면 기존 힌트를 반환한다.
    /// 최초 생성 조건 미충족 시 null을 반환한다.
    /// </summary>
    /// <param name="context">현재 대화 컨텍스트. DynamicCacheRef/CachedMessageCount/UncachedTokenCount 필드가 갱신될 수 있다.</param>
    /// <param name="messages">BuildMessageList 결과 (system + history + new user message).</param>
    /// <param name="tools">LLM에 등록된 툴 목록. 캐시 생성 시 포함됨.</param>
    /// <param name="ct">취소 토큰.</param>
    Task<CacheHint?> TryRollCacheAsync(
        ConversationContext context,
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ILLMTool>? tools,
        CancellationToken ct = default);
}
