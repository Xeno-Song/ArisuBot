using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>
/// MongoDB 기반 유저별 session semantic memory 저장소.
/// document는 session별 생성. state("active"/"inactive")는 수동 관리.
/// </summary>
public class SemanticMemoryRepository : ISemanticMemoryRepository
{
    private readonly IMongoCollection<SemanticMemoryDocument> _collection;

    public SemanticMemoryRepository(MongoDbContext context)
    {
        _collection = context.GetCollection<SemanticMemoryDocument>("semantic_memory");
    }

    /// <summary>
    /// userId의 가장 최신 active document 반환. 없으면 null.
    /// createdAt 내림차순 정렬 후 첫 번째 active document를 선택.
    /// </summary>
    public async Task<UserSemanticMemory?> GetLatestActiveAsync(ulong userId, CancellationToken ct = default)
    {
        var filter = Builders<SemanticMemoryDocument>.Filter.And(
            Builders<SemanticMemoryDocument>.Filter.Eq(d => d.UserId, userId.ToString()),
            Builders<SemanticMemoryDocument>.Filter.Eq(d => d.State, SemanticMemoryState.Active));

        var doc = await _collection
            .Find(filter)
            .SortByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return doc?.ToDomain();
    }

    /// <summary>
    /// 신규 session memory document 삽입. InsertOne 후 생성된 ObjectId를 memory.Id에 기록.
    /// </summary>
    public async Task CreateAsync(UserSemanticMemory memory, CancellationToken ct = default)
    {
        var doc = SemanticMemoryDocument.FromDomain(memory);
        await _collection.InsertOneAsync(doc, null, ct);
        // InsertOne 후 MongoDB가 채운 ObjectId를 도메인 모델에 반영
        memory.Id = doc.Id.ToString();
    }

    /// <summary>지정 document의 state를 변경한다 (수동 rollback/비활성화 용도).</summary>
    public async Task SetStateAsync(string id, string state, CancellationToken ct = default)
    {
        var objectId = ObjectId.Parse(id);
        var filter   = Builders<SemanticMemoryDocument>.Filter.Eq(d => d.Id, objectId);
        var update   = Builders<SemanticMemoryDocument>.Update.Set(d => d.State, state);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}
