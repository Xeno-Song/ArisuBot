using ArisuBot.Dashboard.Services;
using ArisuBot.Infrastructure.MongoDB.Documents;
using ArisuBot.LLM.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Dashboard;

/// <summary>TokenSyncService가 시작 시 동기화하고, 수동 트리거에 반응하는지 검증.</summary>
public class TokenSyncServiceTests
{
    private static TokenSyncService CreateSut(
        Mock<IMongoQueryService>? mongoMock = null,
        DashboardStateService? state = null,
        int intervalMinutes = 60)
    {
        var mongo = mongoMock ?? new Mock<IMongoQueryService>();
        var s     = state ?? new DashboardStateService();
        var opts  = Options.Create(new TokenSyncOptions { IntervalMinutes = intervalMinutes });
        return new TokenSyncService(mongo.Object, s, opts, NullLogger<TokenSyncService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_OnStart_CallsSyncOnce()
    {
        var mongoMock = new Mock<IMongoQueryService>();
        mongoMock
            .Setup(m => m.GetAllSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationDocument>());
        mongoMock
            .Setup(m => m.GetTotalTokenAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenAggregate { TotalIn = 100, TotalOut = 50, TotalCachedIn = 10, RecordCount = 2 });

        var state = new DashboardStateService();
        var sut   = CreateSut(mongoMock, state, intervalMinutes: 60);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);

        // 초기 동기화가 완료될 때까지 잠시 대기
        await Task.Delay(200, CancellationToken.None);

        mongoMock.Verify(
            m => m.GetTotalTokenAggregateAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        var b = state.Baseline;
        Assert.True(b.IsLoaded);
        Assert.Equal(100, b.TotalIn);
        Assert.Equal(50, b.TotalOut);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncNowAsync_TriggersAdditionalSync()
    {
        var callCount = 0;
        var mongoMock = new Mock<IMongoQueryService>();
        mongoMock
            .Setup(m => m.GetTotalTokenAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new TokenAggregate
                {
                    TotalIn = callCount * 100L,
                    TotalOut = callCount * 50L,
                    TotalCachedIn = callCount * 10L,
                    RecordCount = callCount
                };
            });

        var state = new DashboardStateService();
        var sut   = CreateSut(mongoMock, state, intervalMinutes: 60);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);

        // 초기 동기화 대기
        await Task.Delay(200, CancellationToken.None);
        var firstCount = callCount;

        // 수동 트리거
        await sut.SyncNowAsync();
        await Task.Delay(300, CancellationToken.None);

        Assert.True(callCount > firstCount, "SyncNowAsync should trigger an additional sync");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SyncNowAsync_Idempotent_DoesNotQueueMultipleTriggers()
    {
        var mongoMock = new Mock<IMongoQueryService>();
        mongoMock
            .Setup(m => m.GetTotalTokenAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenAggregate());

        var sut = CreateSut(mongoMock, intervalMinutes: 60);

        // SyncNowAsync 여러 번 호출해도 semaphore count 1 초과 안 함 — 예외 없어야 함
        await sut.SyncNowAsync();
        await sut.SyncNowAsync();
        await sut.SyncNowAsync();

        // 예외 없이 통과하면 성공
    }

    [Fact]
    public async Task SyncBaseline_OnSync_ClearsTokenDelta()
    {
        var mongoMock = new Mock<IMongoQueryService>();
        mongoMock
            .Setup(m => m.GetTotalTokenAggregateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenAggregate { TotalIn = 500, TotalOut = 200, TotalCachedIn = 50, RecordCount = 5 });

        var state = new DashboardStateService();
        // delta 쌓기
        state.Apply(new TokenUsageEvent("ctx-1", 100, 50, 10, "m"));
        Assert.Single(state.TokenDelta);

        var sut = CreateSut(mongoMock, state, intervalMinutes: 60);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await sut.StartAsync(cts.Token);

        // 초기 sync 후 delta 리셋 확인
        await Task.Delay(300, CancellationToken.None);

        Assert.Empty(state.TokenDelta);
        Assert.Equal(500, state.Baseline.TotalIn);

        await sut.StopAsync(CancellationToken.None);
    }
}
