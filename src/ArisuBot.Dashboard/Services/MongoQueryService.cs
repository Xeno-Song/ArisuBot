using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ArisuBot.Dashboard.Services;

/// <summary>Dashboard에서 사용하는 MongoDB 읽기 쿼리 추상화. 테스트 대역 주입을 허용한다.</summary>
public interface IMongoQueryService
{
    Task<List<ErrorLogDocument>> GetRecentErrorsAsync(int limit = 100, CancellationToken ct = default);
    Task<List<ErrorLogDocument>> GetErrorsByTypeAsync(string errorType, int limit = 50, CancellationToken ct = default);
    Task<List<ConversationDocument>> GetAllSessionsAsync(int limit = 200, CancellationToken ct = default);
    Task<TokenAggregate?> GetSessionTokenAggregateAsync(string sessionId, CancellationToken ct = default);
    Task<TokenAggregate> GetTotalTokenAggregateAsync(CancellationToken ct = default);
}

/// <summary>Dashboard용 MongoDB 읽기 전용 쿼리 서비스. error_logs + conversations 컬렉션 조회.</summary>
public class MongoQueryService : IMongoQueryService
{
    private readonly MongoDbContext _db;

    public MongoQueryService(MongoDbContext db)
    {
        _db = db;
    }

    /// <summary>최근 에러 로그 N건 반환. 최신 순 정렬.</summary>
    public async Task<List<ErrorLogDocument>> GetRecentErrorsAsync(int limit = 100, CancellationToken ct = default)
    {
        var collection = _db.GetCollection<ErrorLogDocument>("error_logs");
        return await collection
            .Find(FilterDefinition<ErrorLogDocument>.Empty)
            .Sort(Builders<ErrorLogDocument>.Sort.Descending(d => d.Timestamp))
            .Limit(limit)
            .ToListAsync(ct);
    }

    /// <summary>에러 타입 필터로 에러 로그 조회.</summary>
    public async Task<List<ErrorLogDocument>> GetErrorsByTypeAsync(
        string errorType, int limit = 50, CancellationToken ct = default)
    {
        var collection = _db.GetCollection<ErrorLogDocument>("error_logs");
        var filter = Builders<ErrorLogDocument>.Filter.Eq(d => d.ErrorType.ToString(), errorType);
        return await collection
            .Find(filter)
            .Sort(Builders<ErrorLogDocument>.Sort.Descending(d => d.Timestamp))
            .Limit(limit)
            .ToListAsync(ct);
    }

    /// <summary>모든 세션 요약 반환 (최신 순). Messages 배열 제외 + 카운트만 집계해 페이로드 최소화.</summary>
    public async Task<List<ConversationDocument>> GetAllSessionsAsync(int limit = 200, CancellationToken ct = default)
    {
        var collection = _db.GetCollection<ConversationDocument>("conversation_contexts");
        return await collection.Aggregate()
            // $addFields: messageCount = messages 배열 크기 ($size)
            .AppendStage<ConversationDocument>(new BsonDocument("$addFields",
                new BsonDocument("messageCount", new BsonDocument("$size", "$messages"))))
            // $project: messages 배열 제외 (페이로드 축소)
            .AppendStage<ConversationDocument>(new BsonDocument("$project",
                new BsonDocument("messages", 0)))
            .SortByDescending(d => d.UpdatedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }

    /// <summary>특정 세션의 토큰 사용량 집계. 세션이 없으면 null 반환.</summary>
    public async Task<TokenAggregate?> GetSessionTokenAggregateAsync(
        string sessionId, CancellationToken ct = default)
    {
        var collection = _db.GetCollection<ConversationDocument>("conversation_contexts");
        var filter = Builders<ConversationDocument>.Filter.Eq(d => d.Id, sessionId);
        var doc = await collection.Find(filter).FirstOrDefaultAsync(ct);
        if (doc is null) return null;

        return new TokenAggregate
        {
            SessionId     = sessionId,
            TotalIn       = doc.TokenUsage.Sum(t => t.TokensIn),
            TotalOut      = doc.TokenUsage.Sum(t => t.TokensOut),
            TotalCachedIn = doc.TokenUsage.Sum(t => t.TokensCachedIn),
            RecordCount   = doc.TokenUsage.Count
        };
    }

    /// <summary>전체 세션 토큰 합계.</summary>
    public async Task<TokenAggregate> GetTotalTokenAggregateAsync(CancellationToken ct = default)
    {
        var sessions = await GetAllSessionsAsync(ct: ct);
        return new TokenAggregate
        {
            TotalIn       = sessions.Sum(s => s.TokenUsage.Sum(t => t.TokensIn)),
            TotalOut      = sessions.Sum(s => s.TokenUsage.Sum(t => t.TokensOut)),
            TotalCachedIn = sessions.Sum(s => s.TokenUsage.Sum(t => t.TokensCachedIn)),
            RecordCount   = sessions.Sum(s => s.TokenUsage.Count)
        };
    }
}

/// <summary>토큰 집계 결과 DTO.</summary>
public class TokenAggregate
{
    public string? SessionId { get; set; }
    public long TotalIn { get; set; }
    public long TotalOut { get; set; }
    public long TotalCachedIn { get; set; }
    public int RecordCount { get; set; }
    public long TotalAll => TotalIn + TotalOut;
}
