namespace ArisuBot.Monitor;

/// <summary>활성 Gemini 명시적 캐시 상태.</summary>
public class CacheEntry
{
    public string CacheName { get; init; } = string.Empty;
    public int TokenCount { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string ContextId { get; init; } = string.Empty;

    /// <summary>현재 시각 기준 잔여 TTL. 음수면 만료됨.</summary>
    public TimeSpan TimeToLive => ExpiresAt - DateTimeOffset.UtcNow;
}
