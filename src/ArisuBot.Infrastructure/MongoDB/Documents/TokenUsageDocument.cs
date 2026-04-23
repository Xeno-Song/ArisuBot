using ArisuBot.Core.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace ArisuBot.Infrastructure.MongoDB.Documents;

/// <summary>토큰 사용량 MongoDB 서브도큐먼트.</summary>
public class TokenUsageDocument
{
    [BsonElement("tokensIn")]
    public int TokensIn { get; set; }

    [BsonElement("tokensOut")]
    public int TokensOut { get; set; }

    [BsonElement("tokensCachedIn")]
    public int TokensCachedIn { get; set; }

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }

    public TokenUsage ToDomain() => new()
    {
        TokensIn = TokensIn,
        TokensOut = TokensOut,
        TokensCachedIn = TokensCachedIn,
        Timestamp = Timestamp
    };

    public static TokenUsageDocument FromDomain(TokenUsage usage) => new()
    {
        TokensIn = usage.TokensIn,
        TokensOut = usage.TokensOut,
        TokensCachedIn = usage.TokensCachedIn,
        Timestamp = usage.Timestamp
    };
}
