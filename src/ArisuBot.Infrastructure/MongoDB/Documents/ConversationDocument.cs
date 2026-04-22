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

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>도메인 모델로 변환.</summary>
    public ConversationContext ToDomain() => new()
    {
        Id = Id,
        Type = Enum.Parse<ContextType>(Type, ignoreCase: true),
        TargetId = ulong.Parse(TargetId),
        Messages = Messages.Select(m => m.ToDomain()).ToList(),
        UpdatedAt = UpdatedAt
    };

    /// <summary>도메인 모델에서 도큐먼트 생성.</summary>
    public static ConversationDocument FromDomain(ConversationContext context) => new()
    {
        // 빈 Id는 ObjectId 직렬화 실패를 막기 위해 새 값 생성. SaveContextAsync가 insert 후 context.Id를 갱신한다.
        Id = string.IsNullOrEmpty(context.Id) ? ObjectId.GenerateNewId().ToString() : context.Id,
        Type = context.Type.ToString(),
        TargetId = context.TargetId.ToString(),
        Messages = context.Messages.Select(ChatMessageDocument.FromDomain).ToList(),
        UpdatedAt = context.UpdatedAt
    };
}

/// <summary>ChatMessage MongoDB 서브도큐먼트.</summary>
public class ChatMessageDocument
{
    [BsonElement("role")]
    public string Role { get; set; } = string.Empty;

    [BsonElement("content")]
    public string Content { get; set; } = string.Empty;

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }

    public ChatMessage ToDomain() => new()
    {
        Role = Enum.Parse<Core.Models.Role>(Role, ignoreCase: true),
        Content = Content,
        Timestamp = Timestamp
    };

    public static ChatMessageDocument FromDomain(ChatMessage message) => new()
    {
        Role = message.Role.ToString(),
        Content = message.Content,
        Timestamp = message.Timestamp
    };
}
