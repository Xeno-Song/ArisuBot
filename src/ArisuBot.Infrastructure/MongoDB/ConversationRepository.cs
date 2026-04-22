using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>MongoDB 기반 대화 컨텍스트 저장소.</summary>
public class ConversationRepository : IConversationRepository
{
    private readonly IMongoCollection<ConversationDocument> _collection;

    public ConversationRepository(MongoDbContext context)
    {
        _collection = context.GetCollection<ConversationDocument>("conversation_contexts");
    }

    public Task<ConversationContext> GetUserContextAsync(ulong userId, CancellationToken ct = default)
        => GetOrCreateAsync(userId.ToString(), ContextType.User, ct);

    public Task<ConversationContext> GetChannelContextAsync(ulong channelId, CancellationToken ct = default)
        => GetOrCreateAsync(channelId.ToString(), ContextType.Channel, ct);

    public async Task SaveContextAsync(ConversationContext context, CancellationToken ct = default)
    {
        var document = ConversationDocument.FromDomain(context);

        if (string.IsNullOrEmpty(context.Id))
        {
            // 신규 컨텍스트: insert 후 생성된 Id를 domain 객체에 반영
            await _collection.InsertOneAsync(document, cancellationToken: ct);
            context.Id = document.Id;
        }
        else
        {
            // 기존 컨텍스트: _id 기준으로 replace
            var filter = Builders<ConversationDocument>.Filter.Eq(d => d.Id, context.Id);
            await _collection.ReplaceOneAsync(filter, document, cancellationToken: ct);
        }
    }

    public async Task ClearContextAsync(ConversationContext context, CancellationToken ct = default)
    {
        context.Messages.Clear();
        context.UpdatedAt = DateTime.UtcNow;
        await SaveContextAsync(context, ct);
    }

    /// <summary>컨텍스트가 없으면 빈 컨텍스트를 반환한다. DB 저장은 첫 SaveContextAsync 호출 시 수행.</summary>
    private async Task<ConversationContext> GetOrCreateAsync(
        string targetId, ContextType type, CancellationToken ct)
    {
        var filter = BuildFilter(targetId, type);
        var document = await _collection.Find(filter).FirstOrDefaultAsync(ct);

        return document?.ToDomain() ?? new ConversationContext
        {
            TargetId = ulong.Parse(targetId),
            Type = type
        };
    }

    private static FilterDefinition<ConversationDocument> BuildFilter(string targetId, ContextType type) =>
        Builders<ConversationDocument>.Filter.And(
            Builders<ConversationDocument>.Filter.Eq(d => d.TargetId, targetId),
            Builders<ConversationDocument>.Filter.Eq(d => d.Type, type.ToString())
        );
}
