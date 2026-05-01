using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Dashboard.Services;

/// <summary>
/// 시작 시 및 주기적으로 MongoDB에서 토큰 집계를 읽어 DashboardStateService baseline을 갱신한다.
/// UI "DB 동기화" 버튼에서도 SyncNowAsync()를 직접 호출할 수 있다.
/// </summary>
public sealed class TokenSyncService : BackgroundService
{
    private readonly IMongoQueryService _mongo;
    private readonly DashboardStateService _state;
    private readonly TokenSyncOptions _options;
    private readonly ILogger<TokenSyncService> _logger;

    // 수동 동기화 요청 신호 — Set 시 대기 중인 루프가 즉시 동기화 수행
    private readonly SemaphoreSlim _manualTrigger = new(0, 1);

    public TokenSyncService(
        IMongoQueryService mongo,
        DashboardStateService state,
        IOptions<TokenSyncOptions> options,
        ILogger<TokenSyncService> logger)
    {
        _mongo   = mongo;
        _state   = state;
        _options = options.Value;
        _logger  = logger;
    }

    /// <summary>UI 버튼에서 수동 동기화 트리거. 이미 대기 중이면 중복 신호 무시.</summary>
    public Task SyncNowAsync()
    {
        // CurrentCount가 이미 1이면 Release 호출 불필요 (semaphore maxCount=1)
        if (_manualTrigger.CurrentCount == 0)
            _manualTrigger.Release(1);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 시작 즉시 첫 번째 동기화
        await RunSyncAsync(ct);

        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);

        while (!ct.IsCancellationRequested)
        {
            // 주기 대기 또는 수동 트리거 — 둘 중 먼저 오는 것에 반응
            var delayTask   = Task.Delay(interval, ct);
            var triggerTask = _manualTrigger.WaitAsync(ct);

            try
            {
                await Task.WhenAny(delayTask, triggerTask);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;

            await RunSyncAsync(ct);
        }
    }

    /// <summary>MongoDB에서 전체 토큰 집계를 읽어 baseline 갱신.</summary>
    private async Task RunSyncAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("토큰 DB 동기화 시작");
            var agg = await _mongo.GetTotalTokenAggregateAsync(ct);
            _state.SyncBaseline(agg.TotalIn, agg.TotalOut, agg.TotalCachedIn, agg.RecordCount);
            _logger.LogInformation(
                "토큰 DB 동기화 완료 — in={In} out={Out} cached={Cached} records={Records}",
                agg.TotalIn, agg.TotalOut, agg.TotalCachedIn, agg.RecordCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "토큰 DB 동기화 실패");
        }
    }
}

/// <summary>TokenSyncService 설정.</summary>
public class TokenSyncOptions
{
    public const string SectionName = "TokenSync";
    /// <summary>주기적 DB 동기화 간격 (분). 기본 5분.</summary>
    public int IntervalMinutes { get; set; } = 5;
}
