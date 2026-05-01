using ArisuBot.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>에러 로그 MongoDB 도큐먼트. error_logs 컬렉션에 저장.</summary>
public class ErrorLogDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>에러 분류 문자열 — 열거형을 그대로 저장.</summary>
    [BsonElement("errorType")]
    [BsonRepresentation(BsonType.String)]
    public ErrorType ErrorType { get; set; }

    [BsonElement("source")]
    public string Source { get; set; } = string.Empty;

    [BsonElement("message")]
    public string Message { get; set; } = string.Empty;

    [BsonElement("details")]
    public string? Details { get; set; }

    [BsonElement("contextId")]
    public string? ContextId { get; set; }
}
