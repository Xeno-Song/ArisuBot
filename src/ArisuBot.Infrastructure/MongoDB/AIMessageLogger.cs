using ArisuBot.Core.Interfaces;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>AI 메시지 입출력을 MongoDB ai_message_logs 컬렉션에 감사 로그로 저장한다.</summary>
public class AIMessageLogger : IAIMessageLogger
{
    private readonly IMongoCollection<AIMessageLogDocument> _collection;

    public AIMessageLogger(MongoDbContext context)
    {
        _collection = context.GetCollection<AIMessageLogDocument>("ai_message_logs");
    }

    public async Task LogAsync(ulong guildId, ulong channelId, ulong userId,
                               string userMessage, string botResponse, string provider,
                               CancellationToken ct = default)
    {
        var document = new AIMessageLogDocument
        {
            GuildId = guildId.ToString(),
            ChannelId = channelId.ToString(),
            UserId = userId.ToString(),
            UserMessage = userMessage,
            BotResponse = botResponse,
            Provider = provider,
            Timestamp = DateTime.UtcNow
        };
        await _collection.InsertOneAsync(document, cancellationToken: ct);
    }
}
