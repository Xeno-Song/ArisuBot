using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>에러 로그를 MongoDB error_logs 컬렉션에 저장한다.</summary>
public class ErrorLogger : IErrorLogger
{
    private readonly IMongoCollection<ErrorLogDocument> _collection;

    public ErrorLogger(MongoDbContext context)
    {
        _collection = context.GetCollection<ErrorLogDocument>("error_logs");
    }

    /// <inheritdoc/>
    public async Task LogAsync(ErrorLogEntry entry, CancellationToken ct = default)
    {
        var document = new ErrorLogDocument
        {
            Timestamp = entry.Timestamp,
            ErrorType = entry.ErrorType,
            Source    = entry.Source,
            Message   = entry.Message,
            Details   = entry.Details,
            ContextId = entry.ContextId
        };
        await _collection.InsertOneAsync(document, cancellationToken: ct);
    }
}
