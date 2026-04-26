using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.MongoDB.Documents;
using ArisuBot.Infrastructure.Options;
using EphemeralMongo;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace ArisuBot.Tests.Integration.MongoDB;

public class ErrorLoggerTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private ErrorLogger _sut = null!;
    private MongoDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync();
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = _runner.ConnectionString,
            DatabaseName     = "test_db"
        });
        _context = new MongoDbContext(options);
        _sut     = new ErrorLogger(_context);
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LogAsync_InsertsDocument_IntoCollection()
    {
        await _sut.LogAsync(new ErrorLogEntry
        {
            ErrorType = ErrorType.LLMError,
            Source    = "GeminiProvider",
            Message   = "test error"
        });

        var collection = _context.GetCollection<ErrorLogDocument>("error_logs");
        var count = await collection.CountDocumentsAsync(FilterDefinition<ErrorLogDocument>.Empty);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LogAsync_FieldsMatchInput()
    {
        var entry = new ErrorLogEntry
        {
            ErrorType = ErrorType.ToolError,
            Source    = "TestTool",
            Message   = "tool exploded",
            Details   = "stack trace here",
            ContextId = "ctx-123"
        };

        await _sut.LogAsync(entry);

        var collection = _context.GetCollection<ErrorLogDocument>("error_logs");
        var doc = await collection.Find(FilterDefinition<ErrorLogDocument>.Empty).FirstAsync();

        Assert.Equal(ErrorType.ToolError, doc.ErrorType);
        Assert.Equal("TestTool", doc.Source);
        Assert.Equal("tool exploded", doc.Message);
        Assert.Equal("stack trace here", doc.Details);
        Assert.Equal("ctx-123", doc.ContextId);
    }

    [Fact]
    public async Task LogAsync_NullOptionalFields_StoredAsNull()
    {
        await _sut.LogAsync(new ErrorLogEntry
        {
            ErrorType = ErrorType.CacheError,
            Source    = "GeminiCacheManager",
            Message   = "cache failed"
            // Details, ContextId 생략 → null
        });

        var collection = _context.GetCollection<ErrorLogDocument>("error_logs");
        var doc = await collection.Find(FilterDefinition<ErrorLogDocument>.Empty).FirstAsync();

        Assert.Null(doc.Details);
        Assert.Null(doc.ContextId);
    }

    [Fact]
    public async Task LogAsync_Timestamp_IsRecentUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _sut.LogAsync(new ErrorLogEntry
        {
            ErrorType = ErrorType.SystemError,
            Source    = "Test",
            Message   = "ts test"
        });

        var collection = _context.GetCollection<ErrorLogDocument>("error_logs");
        var doc = await collection.Find(FilterDefinition<ErrorLogDocument>.Empty).FirstAsync();

        Assert.True(doc.Timestamp >= before);
        Assert.True(doc.Timestamp <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task LogAsync_MultipleLogs_InsertMultipleDocuments()
    {
        await _sut.LogAsync(new ErrorLogEntry { ErrorType = ErrorType.LLMError,   Source = "A", Message = "1" });
        await _sut.LogAsync(new ErrorLogEntry { ErrorType = ErrorType.ToolError,  Source = "B", Message = "2" });
        await _sut.LogAsync(new ErrorLogEntry { ErrorType = ErrorType.CacheError, Source = "C", Message = "3" });

        var collection = _context.GetCollection<ErrorLogDocument>("error_logs");
        var count = await collection.CountDocumentsAsync(FilterDefinition<ErrorLogDocument>.Empty);
        Assert.Equal(3, count);
    }
}
