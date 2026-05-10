using ArisuBot.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>
/// 유저별 session semantic memory MongoDB 도큐먼트.
/// session별로 신규 생성. state("active"/"inactive")는 수동 관리.
/// LLM 주입 시 최신 active document의 snapshot 사용.
/// </summary>
[BsonIgnoreExtraElements]
public class SemanticMemoryDocument
{
    /// <summary>MongoDB ObjectId. InsertOne 시 자동 생성.</summary>
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>Discord User ID (문자열 저장).</summary>
    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>이 memory를 추출한 conversation session ID.</summary>
    [BsonElement("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>"active" 또는 "inactive". 수동으로만 변경.</summary>
    [BsonElement("state")]
    public string State { get; set; } = SemanticMemoryState.Active;

    /// <summary>이번 session에서 새로 추출된 데이터 (rollback 기준점).</summary>
    [BsonElement("extracted")]
    public SemanticMemoryDataDocument Extracted { get; set; } = new();

    /// <summary>이전 최신 snapshot + Extracted 누적 결과. LLM 주입에 사용.</summary>
    [BsonElement("snapshot")]
    public SemanticMemoryDataDocument Snapshot { get; set; } = new();

    /// <summary>생성 시각 (UTC). GetLatestActiveAsync 정렬 기준.</summary>
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>도메인 모델로 변환.</summary>
    public UserSemanticMemory ToDomain() => new()
    {
        Id        = Id.ToString(),
        UserId    = ulong.Parse(UserId),
        SessionId = SessionId,
        State     = State,
        Extracted = Extracted.ToDomain(),
        Snapshot  = Snapshot.ToDomain(),
        CreatedAt = DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)
    };

    /// <summary>도메인 모델에서 document 생성. Id는 MongoDB가 채운다.</summary>
    public static SemanticMemoryDocument FromDomain(UserSemanticMemory memory) => new()
    {
        UserId    = memory.UserId.ToString(),
        SessionId = memory.SessionId,
        State     = memory.State,
        Extracted = SemanticMemoryDataDocument.FromDomain(memory.Extracted),
        Snapshot  = SemanticMemoryDataDocument.FromDomain(memory.Snapshot),
        CreatedAt = memory.CreatedAt
    };
}

/// <summary>SemanticMemoryData MongoDB 서브도큐먼트. traits/episodic 두 목록 보관.</summary>
[BsonIgnoreExtraElements]
public class SemanticMemoryDataDocument
{
    /// <summary>영속적 성향·선호·습관 목록.</summary>
    [BsonElement("traits")]
    public List<string> Traits { get; set; } = new();

    /// <summary>경험·사건·에피소드 목록.</summary>
    [BsonElement("episodic")]
    public List<string> Episodic { get; set; } = new();

    /// <summary>도메인 모델로 변환. 방어적 복사.</summary>
    public SemanticMemoryData ToDomain() => new()
    {
        Traits   = new List<string>(Traits),
        Episodic = new List<string>(Episodic)
    };

    /// <summary>도메인 모델에서 서브도큐먼트 생성. 방어적 복사.</summary>
    public static SemanticMemoryDataDocument FromDomain(SemanticMemoryData data) => new()
    {
        Traits   = new List<string>(data.Traits),
        Episodic = new List<string>(data.Episodic)
    };
}
