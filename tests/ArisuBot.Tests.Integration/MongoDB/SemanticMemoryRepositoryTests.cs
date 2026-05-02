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

    /// <summary>테스트용 UserSemanticMemory 빌더. 최소한의 값만 지정.</summary>
    private static UserSemanticMemory MakeMemory(
        ulong userId,
        string sessionId = "session001",
        string state     = SemanticMemoryState.Active,
        SemanticMemoryData? snapshot  = null,
        SemanticMemoryData? extracted = null) => new()
    {
        UserId    = userId,
        SessionId = sessionId,
        State     = state,
        Extracted = extracted ?? new SemanticMemoryData(),
        Snapshot  = snapshot  ?? new SemanticMemoryData(),
        CreatedAt = DateTime.UtcNow
    };

    // =========================================================
    // GetLatestActiveAsync
    // =========================================================

    [Fact]
    public async Task GetLatestActiveAsync_NoDocuments_ReturnsNull()
    {
        var result = await _sut.GetLatestActiveAsync(999UL);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestActiveAsync_SingleActive_ReturnsIt()
    {
        var memory = MakeMemory(100UL, snapshot: new SemanticMemoryData { Traits = ["Alice likes cats"] });
        await _sut.CreateAsync(memory);

        var result = await _sut.GetLatestActiveAsync(100UL);

        Assert.NotNull(result);
        Assert.Equal(100UL, result.UserId);
        Assert.Contains("Alice likes cats", result.Snapshot.Traits);
    }

    [Fact]
    public async Task GetLatestActiveAsync_MultipleActive_ReturnsLatest()
    {
        // 두 document 생성 — createdAt 차이를 보장하기 위해 순차 삽입
        var older = MakeMemory(200UL, sessionId: "sess1",
            snapshot: new SemanticMemoryData { Traits = ["old trait"] });
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        await _sut.CreateAsync(older);

        var newer = MakeMemory(200UL, sessionId: "sess2",
            snapshot: new SemanticMemoryData { Traits = ["new trait"] });
        await _sut.CreateAsync(newer);

        var result = await _sut.GetLatestActiveAsync(200UL);

        Assert.NotNull(result);
        Assert.Equal("sess2", result.SessionId);
        Assert.Contains("new trait", result.Snapshot.Traits);
    }

    [Fact]
    public async Task GetLatestActiveAsync_AllInactive_ReturnsNull()
    {
        var memory = MakeMemory(300UL, state: SemanticMemoryState.Inactive);
        await _sut.CreateAsync(memory);

        var result = await _sut.GetLatestActiveAsync(300UL);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestActiveAsync_MixedStates_ReturnsLatestActive()
    {
        // inactive document
        var inactive = MakeMemory(400UL, sessionId: "sess1", state: SemanticMemoryState.Inactive,
            snapshot: new SemanticMemoryData { Traits = ["old"] });
        inactive.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        await _sut.CreateAsync(inactive);

        // active document (older)
        var active = MakeMemory(400UL, sessionId: "sess2", state: SemanticMemoryState.Active,
            snapshot: new SemanticMemoryData { Traits = ["active"] });
        active.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        await _sut.CreateAsync(active);

        var result = await _sut.GetLatestActiveAsync(400UL);

        Assert.NotNull(result);
        Assert.Contains("active", result.Snapshot.Traits);
    }

    // =========================================================
    // CreateAsync
    // =========================================================

    [Fact]
    public async Task CreateAsync_NewDocument_CanBeRetrieved()
    {
        var memory = MakeMemory(500UL,
            snapshot: new SemanticMemoryData
            {
                Traits   = ["Alice likes cats"],
                Episodic = ["Alice visited Tokyo"]
            },
            extracted: new SemanticMemoryData
            {
                Traits = ["Alice likes cats"]
            });
        await _sut.CreateAsync(memory);

        var result = await _sut.GetLatestActiveAsync(500UL);

        Assert.NotNull(result);
        Assert.Contains("Alice likes cats",   result.Snapshot.Traits);
        Assert.Contains("Alice visited Tokyo", result.Snapshot.Episodic);
        Assert.Contains("Alice likes cats",   result.Extracted.Traits);
    }

    [Fact]
    public async Task CreateAsync_PopulatesIdOnMemory()
    {
        var memory = MakeMemory(501UL);
        Assert.Equal(string.Empty, memory.Id);

        await _sut.CreateAsync(memory);

        Assert.NotEmpty(memory.Id);
    }

    [Fact]
    public async Task CreateAsync_SetsStateActive_ByDefault()
    {
        var memory = MakeMemory(502UL);
        await _sut.CreateAsync(memory);

        var result = await _sut.GetLatestActiveAsync(502UL);

        Assert.NotNull(result);
        Assert.Equal(SemanticMemoryState.Active, result.State);
    }

    // =========================================================
    // SetStateAsync
    // =========================================================

    [Fact]
    public async Task SetStateAsync_ActiveToInactive_DocumentNoLongerReturnedByGetLatestActive()
    {
        var memory = MakeMemory(600UL);
        await _sut.CreateAsync(memory);

        await _sut.SetStateAsync(memory.Id, SemanticMemoryState.Inactive);

        var result = await _sut.GetLatestActiveAsync(600UL);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetStateAsync_InactiveToActive_DocumentReturnedByGetLatestActive()
    {
        var memory = MakeMemory(700UL, state: SemanticMemoryState.Inactive);
        await _sut.CreateAsync(memory);

        await _sut.SetStateAsync(memory.Id, SemanticMemoryState.Active);

        var result = await _sut.GetLatestActiveAsync(700UL);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SetStateAsync_OnlyTargetDocumentAffected()
    {
        // 두 document 생성
        var mem1 = MakeMemory(800UL, sessionId: "sess1");
        var mem2 = MakeMemory(800UL, sessionId: "sess2");
        await _sut.CreateAsync(mem1);
        await _sut.CreateAsync(mem2);

        // mem1만 비활성화
        await _sut.SetStateAsync(mem1.Id, SemanticMemoryState.Inactive);

        // 최신 active = mem2
        var result = await _sut.GetLatestActiveAsync(800UL);
        Assert.NotNull(result);
        Assert.Equal("sess2", result.SessionId);
    }

    // =========================================================
    // 여러 userId 독립성
    // =========================================================

    [Fact]
    public async Task MultipleUserIds_StoredIndependently()
    {
        await _sut.CreateAsync(MakeMemory(901UL,
            snapshot: new SemanticMemoryData { Traits = ["User 901 trait"] }));
        await _sut.CreateAsync(MakeMemory(902UL,
            snapshot: new SemanticMemoryData { Traits = ["User 902 trait"] }));

        var result901 = await _sut.GetLatestActiveAsync(901UL);
        var result902 = await _sut.GetLatestActiveAsync(902UL);

        Assert.Contains("User 901 trait", result901!.Snapshot.Traits);
        Assert.Contains("User 902 trait", result902!.Snapshot.Traits);
    }
}
