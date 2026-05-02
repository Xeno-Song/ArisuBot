using ArisuBot.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>유저별 semantic memory MongoDB 도큐먼트. Discord User ID를 primary key로 사용한다.</summary>
[BsonIgnoreExtraElements]
public class SemanticMemoryDocument
{
    /// <summary>Discord User ID. primary key (_id).</summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>누적된 fact 목록 (trait + event + episode 혼합).</summary>
    [BsonElement("facts")]
    public List<SemanticMemoryFactDocument> Facts { get; set; } = new();

    /// <summary>마지막 갱신 시각 (UTC).</summary>
    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>도메인 모델로 변환.</summary>
    public UserSemanticMemory ToDomain() => new()
    {
        UserId    = ulong.Parse(UserId),
        Facts     = Facts.Select(f => f.ToDomain()).ToList(),
        UpdatedAt = DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)
    };
}

/// <summary>SemanticMemoryFact MongoDB 서브도큐먼트.</summary>
[BsonIgnoreExtraElements]
public class SemanticMemoryFactDocument
{
    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>사실 분류: "trait" | "event" | "episode".</summary>
    [BsonElement("type")]
    public string Type { get; set; } = string.Empty;

    [BsonElement("sourceContextId")]
    public string SourceContextId { get; set; } = string.Empty;

    [BsonElement("extractedAt")]
    public DateTime ExtractedAt { get; set; }

    public SemanticMemoryFact ToDomain() => new()
    {
        Content         = Content,
        Type            = Type,
        SourceContextId = SourceContextId,
        ExtractedAt     = DateTime.SpecifyKind(ExtractedAt, DateTimeKind.Utc)
    };

    public static SemanticMemoryFactDocument FromDomain(SemanticMemoryFact fact) => new()
    {
        Content         = fact.Content,
        Type            = fact.Type,
        SourceContextId = fact.SourceContextId,
        ExtractedAt     = fact.ExtractedAt
    };
}
