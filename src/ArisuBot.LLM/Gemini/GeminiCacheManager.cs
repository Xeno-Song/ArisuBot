using System.Collections.Concurrent;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.LLM.Gemini;

/// <summary>
/// Gemini 명시적 캐시 생명주기 관리. ILLMCacheManager 구현체.
/// velocity + threshold 조건 판단, 캐시 생성/교체/삭제, in-memory 추적을 담당한다.
/// 캐시 생성/삭제 이벤트를 ILlmMonitorServer를 통해 Monitor Sidecar에 전달한다.
/// </summary>
public class GeminiCacheManager : ILLMCacheManager
{
    private readonly IGeminiCacheClient _cacheClient;
    private readonly ILlmMonitorServer _pipeServer;
    private readonly CacheOptions _cacheOptions;
    private readonly GeminiOptions _geminiOptions;
    private readonly ILogger<GeminiCacheManager> _logger;

    // 이 프로세스에서 생성한 캐시 이름 추적 — GeminiCacheCleanupService에서 정상 종료 시 삭제에 사용
    private readonly ConcurrentDictionary<string, byte> _trackedCaches = new();

    public GeminiCacheManager(
        IGeminiCacheClient cacheClient,
        ILlmMonitorServer pipeServer,
        IOptions<CacheOptions> cacheOptions,
        IOptions<GeminiOptions> geminiOptions,
        ILogger<GeminiCacheManager> logger)
    {
        _cacheClient   = cacheClient;
        _pipeServer    = pipeServer;
        _cacheOptions  = cacheOptions.Value;
        _geminiOptions = geminiOptions.Value;
        _logger        = logger;
    }

    /// <inheritdoc/>
    public async Task<CacheHint?> TryRollCacheAsync(
        ConversationContext context,
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ILLMTool>? tools,
        CancellationToken ct = default)
    {
        if (!_cacheOptions.Enabled)
            return null;

        var messageList = messages.ToList();

        // 비-System 메시지 (new user message 포함)
        var nonSystemMessages = messageList.Where(m => m.Role != Role.System).ToList();

        // 캐시에 포함할 이력: 마지막 메시지(new user message)를 제외한 전체 이력
        var historyToCache = nonSystemMessages.Take(nonSystemMessages.Count - 1).ToList();

        // cache 경계를 항상 model role(Assistant/ToolCall)로 맞춤 — trailing User/ToolResponse trim.
        // cache 마지막이 model, 새 요청 첫 메시지가 user → 경계에서 role 충돌 없음.
        while (historyToCache.Count > 0 &&
               historyToCache[^1].Role is Role.User)
            historyToCache.RemoveAt(historyToCache.Count - 1);

        var newCachedMessageCount = historyToCache.Count;

        // velocity window 초과분 prune — 저장 시 불필요한 데이터 축적 방지
        PruneTimestamps(context);

        var hasExistingCache = !string.IsNullOrEmpty(context.DynamicCacheRef);

        // 기존 캐시 있음 → 만료 여부 판단
        if (hasExistingCache)
        {
            var isExpired = context.CacheExpiresAt is null ||
                            DateTimeOffset.UtcNow >= context.CacheExpiresAt.Value;

            if (isExpired)
            {
                // 만료(또는 만료 시각 불명) → ref 클리어 후 신규 생성 경로로 재진입
                _logger.LogInformation("캐시 만료 감지 — name={CacheName} expiresAt={ExpiresAt} — ref 클리어 후 신규 생성 시도",
                    context.DynamicCacheRef, context.CacheExpiresAt);
                context.DynamicCacheRef    = null;
                context.CachedMessageCount = 0;
                context.CacheExpiresAt     = null;
                hasExistingCache           = false;
                // hasExistingCache=false로 아래 분기 재진입
            }
            else if (context.UncachedTokenCount < _cacheOptions.RefreshThresholdTokens)
            {
                // 유효 + refresh 임계값 미달 → TTL 연장 후 기존 hint 반환
                return await TryExtendCacheTtlAsync(context);
            }
            // 유효 + refresh 임계값 이상 → 아래 rolling 경로 진입 (기존 로직)
        }

        // 기존 캐시 없음 + initial 임계값 미달 → no cache
        if (!hasExistingCache &&
            context.UncachedTokenCount < _cacheOptions.InitialThresholdTokens)
        {
            _logger.LogDebug("초기 캐시 임계값 미달 — no cache (uncachedTokens={Tokens} initialThreshold={Threshold})",
                context.UncachedTokenCount, _cacheOptions.InitialThresholdTokens);
            return null;
        }

        // 임계값 도달 — 기존 캐시 없으면 최초 생성: velocity gate 적용
        if (!hasExistingCache && !IsVelocityMet(context))
        {
            _logger.LogDebug("임계값 도달했으나 velocity 미충족 — 최초 캐시 생성 건너뜀");
            return null;
        }

        // trim 후 빈 경우 — model 응답 없음 → 새 캐시 생성 불가, 기존 캐시 재사용 또는 null
        if (historyToCache.Count == 0)
        {
            _logger.LogDebug("historyToCache 비어있음 (model 응답 없음) — 캐시 생성 건너뜀");
            return string.IsNullOrEmpty(context.DynamicCacheRef)
                ? null
                : new CacheHint(context.DynamicCacheRef, context.CachedMessageCount);
        }

        // 임계값 도달 + 조건 충족 → 새 캐시 생성
        return await CreateOrRollCacheAsync(context, messageList, historyToCache, newCachedMessageCount, tools, ct);
    }

    private async Task<CacheHint?> CreateOrRollCacheAsync(
        ConversationContext context,
        List<ChatMessage> messageList,
        List<ChatMessage> historyToCache,
        int newCachedMessageCount,
        IReadOnlyList<ILLMTool>? tools,
        CancellationToken ct)
    {
        var systemInstruction = GeminiContentBuilder.BuildSystemInstruction(messageList);
        var toolDeclarations  = (tools is { Count: > 0 })
            ? GeminiContentBuilder.BuildToolDeclarations(tools)
            : null;
        var contentsToCache    = GeminiContentBuilder.BuildContents(historyToCache);
        // historyToCache가 model role로 끝남 → 새 user Content와 role 충돌 없음
        // → BuildContents(fullList).Skip(cachedContentCount) 안전하게 사용 가능
        var cachedContentCount = contentsToCache.Count;
        var ttl                = $"{_cacheOptions.TtlSeconds}s";

        Google.GenAI.Types.CachedContent newCache;
        try
        {
            newCache = await _cacheClient.CreateAsync(
                _geminiOptions.Model, systemInstruction, toolDeclarations, contentsToCache, ttl, ct);
            _trackedCaches.TryAdd(newCache.Name!, 0);
            _logger.LogInformation(
                "캐시 생성 완료 — name={CacheName} contentCount={ContentCount} messageCount={MessageCount} ttl={Ttl}",
                newCache.Name, contentsToCache.Count, newCachedMessageCount, ttl);
            _pipeServer.Emit(new CacheCreatedEvent(
                CacheName:  newCache.Name!,
                TokenCount: (int)(newCache.UsageMetadata?.TotalTokenCount ?? 0),
                ContextId:  context.Id,
                ExpiresAt:  DateTimeOffset.UtcNow.AddSeconds(_cacheOptions.TtlSeconds)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "캐시 생성 실패 — 이번 요청은 기존 캐시(있으면) 사용 또는 no-cache로 진행");
            // 기존 캐시 있으면 재사용, 없으면 no cache
            return string.IsNullOrEmpty(context.DynamicCacheRef)
                ? null
                : new CacheHint(context.DynamicCacheRef, context.CachedMessageCount);
        }

        // 이전 캐시 삭제 (교체)
        if (!string.IsNullOrEmpty(context.DynamicCacheRef))
        {
            var oldName = context.DynamicCacheRef;
            try
            {
                await _cacheClient.DeleteAsync(oldName, ct);
                _trackedCaches.TryRemove(oldName, out _);
                _logger.LogInformation("이전 캐시 삭제 완료 — name={CacheName}", oldName);
                _pipeServer.Emit(new CacheDeletedEvent(CacheName: oldName, Reason: "replaced"));
            }
            catch (Exception ex)
            {
                // 이미 만료된 캐시일 수 있음 — 삭제 실패는 무시
                _logger.LogWarning(ex, "이전 캐시 삭제 실패 (무시) — name={CacheName}", oldName);
            }
        }

        // context 캐시 상태 갱신
        context.DynamicCacheRef      = newCache.Name!;
        context.CachedMessageCount   = cachedContentCount;
        context.UncachedTokenCount   = 0;
        context.CacheExpiresAt       = newCache.ExpireTime.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(newCache.ExpireTime.Value, DateTimeKind.Utc))
            : null;

        return new CacheHint(newCache.Name!, cachedContentCount);
    }

    /// <summary>
    /// 기존 캐시의 TTL을 연장한다. 성공 시 CacheExpiresAt을 갱신하고 기존 hint를 반환한다.
    /// 연장 실패 시 경고 로그만 기록하고 기존 hint를 그대로 반환한다.
    /// </summary>
    private async Task<CacheHint> TryExtendCacheTtlAsync(ConversationContext context)
    {
        var ttl = $"{_cacheOptions.TtlSeconds}s";
        try
        {
            var updated = await _cacheClient.UpdateAsync(context.DynamicCacheRef!, ttl);
            context.CacheExpiresAt = updated.ExpireTime.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(updated.ExpireTime.Value, DateTimeKind.Utc))
                : null;
            _logger.LogDebug("캐시 TTL 연장 완료 — name={CacheName} newExpiresAt={ExpiresAt}",
                context.DynamicCacheRef, context.CacheExpiresAt);
            if (context.CacheExpiresAt.HasValue)
                _pipeServer.Emit(new CacheExtendedEvent(
                    CacheName:    context.DynamicCacheRef!,
                    NewExpiresAt: context.CacheExpiresAt.Value));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "캐시 TTL 연장 실패 (무시) — name={CacheName}", context.DynamicCacheRef);
        }

        return new CacheHint(context.DynamicCacheRef!, context.CachedMessageCount);
    }

    /// <summary>velocity 조건 충족 여부. window 내 메시지 수가 VelocityMinMessages 이상이면 true.</summary>
    private bool IsVelocityMet(ConversationContext context)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_cacheOptions.VelocityWindowSeconds);
        var recentCount = context.RecentMessageTimestamps.Count(t => t >= cutoff);
        return recentCount >= _cacheOptions.VelocityMinMessages;
    }

    /// <summary>velocity window 초과분 timestamp 제거.</summary>
    private void PruneTimestamps(ConversationContext context)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_cacheOptions.VelocityWindowSeconds);
        context.RecentMessageTimestamps.RemoveAll(t => t < cutoff);
    }

    /// <summary>이 프로세스에서 추적 중인 캐시 이름 목록. GeminiCacheCleanupService에서 정상 종료 시 삭제에 사용.</summary>
    internal IReadOnlyCollection<string> GetTrackedCacheNames() =>
        _trackedCaches.Keys.ToList();

    /// <summary>지정 캐시를 삭제하고 추적 목록에서 제거한다. 삭제 실패 시 warning 로그 후 무시.</summary>
    internal async Task DeleteTrackedAsync(string cacheName, CancellationToken ct = default)
    {
        try
        {
            await _cacheClient.DeleteAsync(cacheName, ct);
            _trackedCaches.TryRemove(cacheName, out _);
            _logger.LogInformation("캐시 삭제 완료 — name={CacheName}", cacheName);
            _pipeServer.Emit(new CacheDeletedEvent(CacheName: cacheName, Reason: "cleanup"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "캐시 삭제 실패 — name={CacheName}", cacheName);
        }
    }
}
