using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>MongoDB 기반 유저별 semantic memory 저장소.</summary>
public class SemanticMemoryRepository : ISemanticMemoryRepository
{
    private readonly IMongoCollection<SemanticMemoryDocument> _collection;

    public SemanticMemoryRepository(MongoDbContext context)
    {
        _collection = context.GetCollection<SemanticMemoryDocument>("semantic_memory");
    }

    /// <summary>userId에 해당하는 semantic memory 반환. 없으면 null.</summary>
    public async Task<UserSemanticMemory?> GetByUserIdAsync(ulong userId, CancellationToken ct = default)
    {
        var filter = Builders<SemanticMemoryDocument>.Filter.Eq(d => d.UserId, userId.ToString());
        var doc    = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToDomain();
    }

    /// <summary>facts를 userId 도큐먼트에 append upsert. 도큐먼트 없으면 신규 생성.</summary>
    public async Task AppendFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default)
    {
        var factDocs = facts.Select(SemanticMemoryFactDocument.FromDomain).ToList();
        var userIdStr = userId.ToString();

        var filter = Builders<SemanticMemoryDocument>.Filter.Eq(d => d.UserId, userIdStr);
        var update = Builders<SemanticMemoryDocument>.Update
            .PushEach(d => d.Facts, factDocs)
            .Set(d => d.UpdatedAt, DateTime.UtcNow);

        var options = new UpdateOptions { IsUpsert = true };
        await _collection.UpdateOneAsync(filter, update, options, ct);
    }

    /// <summary>userId 도큐먼트의 facts 배열 전체를 교체한다. 압축 완료 후 호출.</summary>
    public async Task ReplaceFactsAsync(ulong userId, IEnumerable<SemanticMemoryFact> facts, CancellationToken ct = default)
    {
        var factDocs = facts.Select(SemanticMemoryFactDocument.FromDomain).ToList();
        var userIdStr = userId.ToString();

        var filter = Builders<SemanticMemoryDocument>.Filter.Eq(d => d.UserId, userIdStr);
        var update = Builders<SemanticMemoryDocument>.Update
            .Set(d => d.Facts, factDocs)
            .Set(d => d.UpdatedAt, DateTime.UtcNow);

        var options = new UpdateOptions { IsUpsert = true };
        await _collection.UpdateOneAsync(filter, update, options, ct);
    }
}
