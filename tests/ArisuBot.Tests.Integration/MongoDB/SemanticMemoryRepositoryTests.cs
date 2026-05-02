using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using EphemeralMongo;
using Microsoft.Extensions.Options;

namespace ArisuBot.Tests.Integration.MongoDB;

public class SemanticMemoryRepositoryTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private SemanticMemoryRepository _sut = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync();
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = _runner.ConnectionString,
            DatabaseName     = "test_db"
        });
        _sut = new SemanticMemoryRepository(new MongoDbContext(options));
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    private static SemanticMemoryFact MakeFact(string content, string type) => new()
    {
        Content         = content,
        Type            = type,
        SourceContextId = "ctx001",
        ExtractedAt     = DateTime.UtcNow
    };

    // =========================================================
    // GetByUserIdAsync
    // =========================================================

    [Fact]
    public async Task GetByUserIdAsync_NotFound_ReturnsNull()
    {
        var result = await _sut.GetByUserIdAsync(999UL);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByUserIdAsync_AfterAppend_ReturnsDocument()
    {
        await _sut.AppendFactsAsync(100UL, [MakeFact("Alice likes cats", "trait")]);

        var result = await _sut.GetByUserIdAsync(100UL);

        Assert.NotNull(result);
        Assert.Equal(100UL, result.UserId);
    }

    // =========================================================
    // AppendFactsAsync
    // =========================================================

    [Fact]
    public async Task AppendFactsAsync_NewUserId_CreatesDocument()
    {
        await _sut.AppendFactsAsync(200UL, [MakeFact("Bob attends gym", "event")]);

        var result = await _sut.GetByUserIdAsync(200UL);

        Assert.NotNull(result);
        Assert.Single(result.Facts);
        Assert.Equal("Bob attends gym", result.Facts[0].Content);
        Assert.Equal("event", result.Facts[0].Type);
    }

    [Fact]
    public async Task AppendFactsAsync_ExistingUserId_AccumulatesFacts()
    {
        await _sut.AppendFactsAsync(300UL, [MakeFact("Trait 1", "trait")]);
        await _sut.AppendFactsAsync(300UL, [MakeFact("Trait 2", "trait"), MakeFact("Event 1", "event")]);

        var result = await _sut.GetByUserIdAsync(300UL);

        Assert.NotNull(result);
        Assert.Equal(3, result.Facts.Count);
    }

    [Fact]
    public async Task AppendFactsAsync_MixedTypes_AllStored()
    {
        var facts = new[]
        {
            MakeFact("Trait",   "trait"),
            MakeFact("Event",   "event"),
            MakeFact("Episode", "episode")
        };
        await _sut.AppendFactsAsync(400UL, facts);

        var result = await _sut.GetByUserIdAsync(400UL);

        Assert.NotNull(result);
        Assert.Equal(3, result.Facts.Count);
        Assert.Contains(result.Facts, f => f.Type == "trait");
        Assert.Contains(result.Facts, f => f.Type == "event");
        Assert.Contains(result.Facts, f => f.Type == "episode");
    }

    // =========================================================
    // ReplaceFactsAsync
    // =========================================================

    [Fact]
    public async Task ReplaceFactsAsync_ExistingDocument_ReplacesAll()
    {
        await _sut.AppendFactsAsync(500UL, [MakeFact("Old fact 1", "trait"), MakeFact("Old fact 2", "event")]);

        await _sut.ReplaceFactsAsync(500UL, [MakeFact("New fact", "trait")]);

        var result = await _sut.GetByUserIdAsync(500UL);

        Assert.NotNull(result);
        Assert.Single(result.Facts);
        Assert.Equal("New fact", result.Facts[0].Content);
    }

    [Fact]
    public async Task ReplaceFactsAsync_NoDocument_CreatesDocument()
    {
        await _sut.ReplaceFactsAsync(600UL, [MakeFact("Compressed trait", "trait")]);

        var result = await _sut.GetByUserIdAsync(600UL);

        Assert.NotNull(result);
        Assert.Single(result.Facts);
    }

    [Fact]
    public async Task ReplaceFactsAsync_EmptyList_ClearsFacts()
    {
        await _sut.AppendFactsAsync(700UL, [MakeFact("some fact", "trait")]);

        await _sut.ReplaceFactsAsync(700UL, []);

        var result = await _sut.GetByUserIdAsync(700UL);

        Assert.NotNull(result);
        Assert.Empty(result.Facts);
    }

    // =========================================================
    // 여러 userId 독립성
    // =========================================================

    [Fact]
    public async Task MultipleUserIds_StoredIndependently()
    {
        await _sut.AppendFactsAsync(801UL, [MakeFact("User 801 fact", "trait")]);
        await _sut.AppendFactsAsync(802UL, [MakeFact("User 802 fact", "event")]);

        var result801 = await _sut.GetByUserIdAsync(801UL);
        var result802 = await _sut.GetByUserIdAsync(802UL);

        Assert.Single(result801!.Facts);
        Assert.Single(result802!.Facts);
        Assert.Equal("User 801 fact", result801.Facts[0].Content);
        Assert.Equal("User 802 fact", result802.Facts[0].Content);
    }
}
