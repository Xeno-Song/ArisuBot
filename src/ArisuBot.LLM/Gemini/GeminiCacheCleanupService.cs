using System.Diagnostics.CodeAnalysis;
using ArisuBot.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArisuBot.LLM.Gemini;

/// <summary>
/// Gemini 명시적 캐시 정리 서비스.
/// StartAsync: Gemini 고아 캐시 일괄 삭제 후 MongoDB dynamicCacheRef 전체 초기화.
/// StopAsync: 이 프로세스에서 생성한 추적 캐시를 전체 삭제한다 (정상 종료 대응).
/// 강제 종료(SIGKILL)는 TTL 만료로 처리.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "실 Gemini API 및 MongoDB 의존 — E2E 테스트 대상.")]
public class GeminiCacheCleanupService : IHostedService
{
    private readonly GeminiCacheManager _cacheManager;
    private readonly IGeminiCacheClient _cacheClient;
    private readonly IConversationRepository _conversationRepository;
    private readonly ILogger<GeminiCacheCleanupService> _logger;

    public GeminiCacheCleanupService(
        GeminiCacheManager cacheManager,
        IGeminiCacheClient cacheClient,
        IConversationRepository conversationRepository,
        ILogger<GeminiCacheCleanupService> logger)
    {
        _cacheManager           = cacheManager;
        _cacheClient            = cacheClient;
        _conversationRepository = conversationRepository;
        _logger                 = logger;
    }

    /// <summary>앱 시작 시 고아 캐시(이전 비정상 종료 잔여분) 전체 삭제.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("시작 시 고아 캐시 정리 시작");
        var count = 0;
        try
        {
            await foreach (var cache in _cacheClient.ListAsync(ct))
            {
                if (cache.Name is null) continue;
                try
                {
                    await _cacheClient.DeleteAsync(cache.Name, ct);
                    count++;
                    _logger.LogDebug("고아 캐시 삭제 — name={CacheName}", cache.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "고아 캐시 삭제 실패 — name={CacheName}", cache.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "고아 캐시 목록 조회 실패 — 정리 건너뜀");
        }
        _logger.LogInformation("시작 시 고아 캐시 정리 완료 — deletedCount={Count}", count);

        // Gemini 캐시 전체 삭제 후 MongoDB stale ref 초기화 — TTL 만료 등으로 캐시가 없어진 경우 포함
        try
        {
            await _conversationRepository.ClearAllDynamicCacheRefsAsync(ct);
            _logger.LogInformation("MongoDB dynamicCacheRef 초기화 완료");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MongoDB dynamicCacheRef 초기화 실패 — stale ref가 남아있을 수 있음");
        }
    }

    /// <summary>앱 정상 종료 시 이 프로세스가 생성한 추적 캐시 전체 삭제.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var names = _cacheManager.GetTrackedCacheNames();
        _logger.LogInformation("종료 시 캐시 삭제 시작 — count={Count}", names.Count);
        foreach (var name in names)
            await _cacheManager.DeleteTrackedAsync(name, ct);
        _logger.LogInformation("종료 시 캐시 삭제 완료");
    }
}
