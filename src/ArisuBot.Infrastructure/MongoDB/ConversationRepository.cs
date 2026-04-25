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

    /// <summary>기존 세션을 보존하고 새 빈 컨텍스트 도큐먼트를 삽입한다.</summary>
    public async Task<ConversationContext> CreateNewSessionAsync(
        ulong targetId, ContextType type, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var newContext = new ConversationContext
        {
            TargetId = targetId,
            Type = type,
            CreatedAt = now,
            UpdatedAt = now
        };
        // SaveContextAsync가 빈 Id를 감지해 insert 처리
        await SaveContextAsync(newContext, ct);
        return newContext;
    }

    /// <summary>가장 최근 생성된 세션을 반환한다. 없으면 빈 컨텍스트 반환(DB 저장은 첫 SaveContextAsync 시 수행).</summary>
    private async Task<ConversationContext> GetOrCreateAsync(
        string targetId, ContextType type, CancellationToken ct)
    {
        var filter = BuildFilter(targetId, type);
        // CreatedAt 내림차순 정렬로 가장 최신 세션 조회
        var document = await _collection
            .Find(filter)
            .SortByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return document?.ToDomain() ?? new ConversationContext
        {
            TargetId = ulong.Parse(targetId),
            Type = type
        };
    }

    /// <summary>
    /// 모든 도큐먼트의 dynamicCacheRef를 제거하고 캐시 카운터를 초기화한다. 프로세스 재시작 시 stale ref 제거에 사용.
    /// UncachedTokenCount는 0이 아닌 LastTotalTokens로 복원 — 다음 요청에서 cache 생성 조건을 즉시 평가하기 위함.
    /// </summary>
    public async Task ClearAllDynamicCacheRefsAsync(CancellationToken ct = default)
    {
        // dynamicCacheRef 필드가 존재하는 도큐먼트만 대상 — 불필요한 write 방지
        var filter = Builders<ConversationDocument>.Filter.Exists(d => d.DynamicCacheRef, true);
        var docs = await _collection.Find(filter).ToListAsync(ct);

        foreach (var doc in docs)
        {
            // UncachedTokenCount = LastTotalTokens: 이전 세션의 실제 context 규모를 복원해
            // 재시작 후 첫 요청에서 cache 생성 조건을 즉시 평가할 수 있도록 한다.
            var docFilter = Builders<ConversationDocument>.Filter.Eq(d => d.Id, doc.Id);
            var update = Builders<ConversationDocument>.Update
                .Unset(d => d.DynamicCacheRef)
                .Set(d => d.CachedMessageCount, 0)
                .Set(d => d.UncachedTokenCount, doc.LastTotalTokens);
            await _collection.UpdateOneAsync(docFilter, update, cancellationToken: ct);
        }
    }

    private static FilterDefinition<ConversationDocument> BuildFilter(string targetId, ContextType type) =>
        Builders<ConversationDocument>.Filter.And(
            Builders<ConversationDocument>.Filter.Eq(d => d.TargetId, targetId),
            Builders<ConversationDocument>.Filter.Eq(d => d.Type, type.ToString())
        );
}
