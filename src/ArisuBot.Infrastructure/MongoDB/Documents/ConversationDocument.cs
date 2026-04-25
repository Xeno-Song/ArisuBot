using ArisuBot.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>대화 컨텍스트 MongoDB 도큐먼트. 도메인 모델과 분리해 BSON 세부사항을 격리한다.</summary>
[BsonIgnoreExtraElements]
public class ConversationDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Discord ulong ID를 문자열로 저장해 BSON Int64 부호 손실을 방지한다.</summary>
    [BsonElement("targetId")]
    public string TargetId { get; set; } = string.Empty;

    [BsonElement("messages")]
    public List<ChatMessageDocument> Messages { get; set; } = new();

    /// <summary>이 세션에서 발생한 LLM 요청별 토큰 사용량 이력.</summary>
    [BsonElement("tokenUsage")]
    public List<TokenUsageDocument> TokenUsage { get; set; } = new();

    /// <summary>대화 참여자 매핑. key: displayName, value: Discord userId(string). ulong BSON 부호 손실 방지를 위해 string으로 저장.</summary>
    [BsonElement("participants")]
    public Dictionary<string, string> Participants { get; set; } = new();

    /// <summary>세션 생성 시각. 세션 간 순서 식별에 사용.</summary>
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    // --- 명시적 캐시 상태 (기존 문서에 없으면 null/0 기본값 적용) ---

    /// <summary>Gemini 명시적 캐시 리소스 이름. 없으면 null.</summary>
    [BsonElement("dynamicCacheRef")]
    [BsonIgnoreIfNull]
    public string? DynamicCacheRef { get; set; }

    /// <summary>DynamicCacheRef에 포함된 비-System 메시지 수.</summary>
    [BsonElement("cachedMessageCount")]
    public int CachedMessageCount { get; set; }

    /// <summary>마지막 캐시 생성 이후 누적된 비캐시 입력 토큰 수.</summary>
    [BsonElement("uncachedTokenCount")]
    public int UncachedTokenCount { get; set; }

    /// <summary>최근 사용자 메시지 수신 시각 목록 (UTC DateTime). velocity 조건 판단에 사용.</summary>
    [BsonElement("recentMessageTimestamps")]
    public List<DateTime> RecentMessageTimestamps { get; set; } = new();

    /// <summary>도메인 모델로 변환.</summary>
    public ConversationContext ToDomain() => new()
    {
        Id = Id,
        Type = Enum.Parse<ContextType>(Type, ignoreCase: true),
        TargetId = ulong.Parse(TargetId),
        Messages = Messages.Select(m => m.ToDomain()).ToList(),
        TokenUsage = TokenUsage.Select(t => t.ToDomain()).ToList(),
        Participants = Participants.ToDictionary(kvp => kvp.Key, kvp => ulong.Parse(kvp.Value)),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DynamicCacheRef            = DynamicCacheRef,
        CachedMessageCount         = CachedMessageCount,
        UncachedTokenCount         = UncachedTokenCount,
        RecentMessageTimestamps    = RecentMessageTimestamps
            .Select(dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)))
            .ToList()
    };

    /// <summary>도메인 모델에서 도큐먼트 생성.</summary>
    public static ConversationDocument FromDomain(ConversationContext context) => new()
    {
        // 빈 Id는 ObjectId 직렬화 실패를 막기 위해 새 값 생성. SaveContextAsync가 insert 후 context.Id를 갱신한다.
        Id = string.IsNullOrEmpty(context.Id) ? ObjectId.GenerateNewId().ToString() : context.Id,
        Type = context.Type.ToString(),
        TargetId = context.TargetId.ToString(),
        Messages = context.Messages.Select(ChatMessageDocument.FromDomain).ToList(),
        TokenUsage = context.TokenUsage.Select(TokenUsageDocument.FromDomain).ToList(),
        Participants = context.Participants.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString()),
        CreatedAt = context.CreatedAt,
        UpdatedAt = context.UpdatedAt,
        DynamicCacheRef         = context.DynamicCacheRef,
        CachedMessageCount      = context.CachedMessageCount,
        UncachedTokenCount      = context.UncachedTokenCount,
        RecentMessageTimestamps = context.RecentMessageTimestamps
            .Select(dto => dto.UtcDateTime)
            .ToList()
    };
}

/// <summary>ChatMessage MongoDB 서브도큐먼트.</summary>
[BsonIgnoreExtraElements]
public class ChatMessageDocument
{
    [BsonElement("role")]
    public string Role { get; set; } = string.Empty;

    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>메시지 발신자 표시 이름. null이면 발신자 정보 없음(System/Assistant 또는 레거시 문서).</summary>
    [BsonElement("senderName")]
    public string? SenderName { get; set; }

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }

    // Tool 전용 필드 — Role.ToolCall / ToolResponse에만 설정됨
    /// <summary>Tool 호출 식별자 (GUID 문자열). ToolCall/ToolResponse 쌍 연결에 사용.</summary>
    [BsonElement("callId")]
    public string? CallId { get; set; }

    /// <summary>Tool 이름. Role.ToolCall, Role.ToolResponse에서 설정.</summary>
    [BsonElement("toolName")]
    public string? ToolName { get; set; }

    /// <summary>Tool 호출 인자 JSON 문자열. Role.ToolCall에서만 설정.</summary>
    [BsonElement("toolArgsJson")]
    public string? ToolArgsJson { get; set; }

    /// <summary>Provider별 보조 메타데이터 JSON. Gemini thought_signature 등 텍스트로 표현 못하는 Part 직렬화 보존.</summary>
    [BsonElement("providerMetadataJson")]
    public string? ProviderMetadataJson { get; set; }

    public ChatMessage ToDomain() => new()
    {
        Role = Enum.Parse<Core.Models.Role>(Role, ignoreCase: true),
        Content = Content,
        SenderName = SenderName,
        Timestamp = Timestamp,
        CallId = CallId is not null ? Guid.Parse(CallId) : null,
        ToolName = ToolName,
        ToolArgsJson = ToolArgsJson,
        ProviderMetadataJson = ProviderMetadataJson
    };

    public static ChatMessageDocument FromDomain(ChatMessage message) => new()
    {
        Role = message.Role.ToString(),
        Content = message.Content,
        SenderName = message.SenderName,
        Timestamp = message.Timestamp,
        CallId = message.CallId?.ToString(),
        ToolName = message.ToolName,
        ToolArgsJson = message.ToolArgsJson,
        ProviderMetadataJson = message.ProviderMetadataJson
    };
}
