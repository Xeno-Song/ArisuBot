using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>AI 메시지 입출력 감사 로그 MongoDB 도큐먼트.</summary>
public class AIMessageLogDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonElement("guildId")]
    public string GuildId { get; set; } = string.Empty;

    [BsonElement("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [BsonElement("userId")]
    public string UserId { get; set; } = string.Empty;

    [BsonElement("userMessage")]
    public string UserMessage { get; set; } = string.Empty;

    [BsonElement("botResponse")]
    public string BotResponse { get; set; } = string.Empty;

    [BsonElement("provider")]
    public string Provider { get; set; } = string.Empty;

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }
}
