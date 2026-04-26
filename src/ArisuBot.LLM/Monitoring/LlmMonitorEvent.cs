using System.Text.Json.Serialization;

namespace ArisuBot.LLM.Monitoring;

/// <summary>Named Pipe로 LLM Monitor Sidecar에 전송하는 이벤트 기반 클래스.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CacheCreatedEvent),  "CACHE_CREATED")]
[JsonDerivedType(typeof(CacheDeletedEvent),  "CACHE_DELETED")]
[JsonDerivedType(typeof(CacheExtendedEvent), "CACHE_EXTENDED")]
[JsonDerivedType(typeof(TokenUsageEvent),    "TOKEN_USAGE")]
[JsonDerivedType(typeof(ModelStatusEvent),   "MODEL_STATUS")]
public abstract record LlmMonitorEvent(DateTimeOffset Timestamp);

/// <summary>Gemini 명시적 캐시 생성 시 발행.</summary>
public sealed record CacheCreatedEvent(
    string CacheName,
    int TokenCount,
    string ContextId,
    DateTimeOffset ExpiresAt)
    : LlmMonitorEvent(DateTimeOffset.UtcNow);

/// <summary>Gemini 명시적 캐시 TTL 연장 성공 시 발행.</summary>
public sealed record CacheExtendedEvent(
    string CacheName,
    DateTimeOffset NewExpiresAt)
    : LlmMonitorEvent(DateTimeOffset.UtcNow);

/// <summary>Gemini 명시적 캐시 삭제 시 발행.</summary>
public sealed record CacheDeletedEvent(
    string CacheName,
    string Reason)
    : LlmMonitorEvent(DateTimeOffset.UtcNow);

/// <summary>LLM 응답 완료 후 토큰 사용량 발행. 스트리밍 완료 후 yield 시마다 1회 발행.</summary>
public sealed record TokenUsageEvent(
    string? ContextId,
    int TokensIn,
    int TokensOut,
    int TokensCached,
    string Model)
    : LlmMonitorEvent(DateTimeOffset.UtcNow);

/// <summary>Fallback 모델로 전환 시 발행.</summary>
public sealed record ModelStatusEvent(
    string CurrentModel,
    string PreviousModel)
    : LlmMonitorEvent(DateTimeOffset.UtcNow);
