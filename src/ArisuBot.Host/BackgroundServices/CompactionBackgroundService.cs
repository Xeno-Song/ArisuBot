using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArisuBot.Host.BackgroundServices;

/// <summary>
/// Inactivity / Scheduled 트리거 기반 Compaction 백그라운드 서비스.
/// 1분 간격으로 모든 active session을 순회해 트리거 조건을 평가하고 실행한다.
/// Token 기반 트리거는 MessageHandler에서 인라인 처리하므로 여기서는 평가하지 않는다.
/// </summary>
public class CompactionBackgroundService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly IConversationRepository   _repository;
    private readonly ICompactionService        _compactionService;
    private readonly ISemanticMemoryService    _semanticMemoryService;
    private readonly CompactionTriggerEvaluator _triggerEvaluator;
    private readonly ILogger<CompactionBackgroundService> _logger;

    public CompactionBackgroundService(
        IConversationRepository repository,
        ICompactionService compactionService,
        ISemanticMemoryService semanticMemoryService,
        CompactionTriggerEvaluator triggerEvaluator,
        ILogger<CompactionBackgroundService> logger)
    {
        _repository             = repository;
        _compactionService      = compactionService;
        _semanticMemoryService  = semanticMemoryService;
        _triggerEvaluator       = triggerEvaluator;
        _logger                 = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CompactionBackgroundService 시작.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            try
            {
                await RunCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CompactionBackgroundService 순회 중 오류 발생.");
            }
        }

        _logger.LogInformation("CompactionBackgroundService 종료.");
    }

    /// <summary>모든 active session 순회 후 Inactivity / Scheduled 조건 평가 및 Compaction 실행.</summary>
    private async Task RunCheckAsync(CancellationToken ct)
    {
        var contexts = await _repository.GetAllActiveContextsAsync(ct);
        var utcNow   = DateTime.UtcNow;

        foreach (var context in contexts)
        {
            // Token 트리거는 MessageHandler 담당 — 여기서는 Inactivity / Scheduled만 평가
            var inactivityMet = _triggerEvaluator.IsInactivityMet(context, utcNow);
            var scheduledMet  = _triggerEvaluator.IsScheduledMet(context, utcNow);

            if (!inactivityMet && !scheduledMet) continue;

            // ShouldCompact: Enabled 체크 + Cooldown 적용
            if (!_triggerEvaluator.ShouldCompact(context, utcNow)) continue;

            _logger.LogInformation(
                "[BackgroundCompaction] 트리거 감지 — contextId={Id} inactivity={Inactivity} scheduled={Scheduled}",
                context.Id, inactivityMet, scheduledMet);

            try
            {
                await _compactionService.RunAsync(context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[BackgroundCompaction] Compaction 실패 — contextId={Id}", context.Id);
                continue;
            }

            // Compaction 성공 후 독립 실행 — 실패해도 루프 계속
            try
            {
                await _semanticMemoryService.ExtractAndSaveAsync(context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[BackgroundCompaction] SemanticMemory 추출 실패 — contextId={Id}", context.Id);
            }
        }
    }
}
