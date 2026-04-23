using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.MongoDB.Documents;
using ArisuBot.Infrastructure.Options;
using EphemeralMongo;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace ArisuBot.Tests.Integration.MongoDB;

public class AIMessageLoggerTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private AIMessageLogger _sut = null!;
    private MongoDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync();
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = _runner.ConnectionString,
            DatabaseName = "test_db"
        });
        _context = new MongoDbContext(options);
        _sut = new AIMessageLogger(_context);
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LogAsync_InsertsDocument_IntoCollection()
    {
        await _sut.LogAsync(
            guildId: 10UL, channelId: 20UL, userId: 30UL,
            userMessage: "hello", botResponse: "hi there", provider: "Gemini");

        var collection = _context.GetCollection<AIMessageLogDocument>("ai_message_logs");
        var count = await collection.CountDocumentsAsync(FilterDefinition<AIMessageLogDocument>.Empty);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LogAsync_FieldsMatchInput()
    {
        await _sut.LogAsync(
            guildId: 100UL, channelId: 200UL, userId: 300UL,
            userMessage: "what is 2+2?", botResponse: "4", provider: "Gemini");

        var collection = _context.GetCollection<AIMessageLogDocument>("ai_message_logs");
        var doc = await collection.Find(FilterDefinition<AIMessageLogDocument>.Empty).FirstAsync();

        Assert.Equal("100", doc.GuildId);
        Assert.Equal("200", doc.ChannelId);
        Assert.Equal("300", doc.UserId);
        Assert.Equal("what is 2+2?", doc.UserMessage);
        Assert.Equal("4", doc.BotResponse);
        Assert.Equal("Gemini", doc.Provider);
    }

    [Fact]
    public async Task LogAsync_Timestamp_IsRecentUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _sut.LogAsync(
            guildId: 0UL, channelId: 0UL, userId: 0UL,
            userMessage: "ts test", botResponse: "ok", provider: "Gemini");

        var collection = _context.GetCollection<AIMessageLogDocument>("ai_message_logs");
        var doc = await collection.Find(FilterDefinition<AIMessageLogDocument>.Empty).FirstAsync();

        Assert.True(doc.Timestamp >= before);
        Assert.True(doc.Timestamp <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task LogAsync_MultipleCalls_InsertMultipleDocuments()
    {
        await _sut.LogAsync(1UL, 1UL, 1UL, "msg1", "resp1", "Gemini");
        await _sut.LogAsync(2UL, 2UL, 2UL, "msg2", "resp2", "Gemini");
        await _sut.LogAsync(3UL, 3UL, 3UL, "msg3", "resp3", "Gemini");

        var collection = _context.GetCollection<AIMessageLogDocument>("ai_message_logs");
        var count = await collection.CountDocumentsAsync(FilterDefinition<AIMessageLogDocument>.Empty);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task LogAsync_GuildId_Zero_StoredAsString()
    {
        // DM 채널은 guildId=0
        await _sut.LogAsync(
            guildId: 0UL, channelId: 500UL, userId: 600UL,
            userMessage: "dm msg", botResponse: "dm resp", provider: "Gemini");

        var collection = _context.GetCollection<AIMessageLogDocument>("ai_message_logs");
        var doc = await collection.Find(FilterDefinition<AIMessageLogDocument>.Empty).FirstAsync();

        Assert.Equal("0", doc.GuildId);
    }
}
