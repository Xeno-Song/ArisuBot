using ArisuBot.LLM.Monitoring;

namespace ArisuBot.Dashboard.Services;

/// <summary>TCP에서 수신한 실시간 이벤트 상태를 in-memory로 관리. Blazor 컴포넌트 갱신 통보.</summary>
public class DashboardStateService
{
    // 실시간 상태
    public bool IsConnected { get; private set; }
    public string CurrentModel { get; private set; } = "?";
    public bool IsFallback { get; private set; }

    // 세션별 실시간 처리 상태 (ProcessingStarted → ProcessingCompleted 기간 동안 active)
    private readonly Dictionary<string, ProcessingState> _activeProcessing = new();

    // 최근 이벤트 로그 (최대 50건)
    private readonly LinkedList<RecentEvent> _recentEvents = new();
    private const int MaxEventCount = 50;

    // 실시간 토큰 집계 (컨텍스트 ID → 누적) — DB 동기화 이후 delta
    private readonly Dictionary<string, SessionTokenSummary> _tokenDelta = new();

    // DB 동기화 기준값 — 마지막 동기화 시점의 MongoDB 전체 누적
    private TokenBaseline _baseline = new();

    // 실시간 tool 호출 에러 (최근 20건)
    private readonly LinkedList<ToolErrorEntry> _toolErrors = new();
    private const int MaxToolErrors = 20;

    private readonly object _lock = new();

    /// <summary>상태 변경 시 Blazor 컴포넌트 갱신을 트리거하는 콜백.</summary>
    public event Action? OnChange;

    public bool IsProcessing
    {
        get { lock (_lock) return _activeProcessing.Count > 0; }
    }

    public IReadOnlyList<ProcessingState> ActiveProcessing
    {
        get { lock (_lock) return _activeProcessing.Values.ToList(); }
    }

    public IReadOnlyList<RecentEvent> RecentEvents
    {
        get { lock (_lock) return _recentEvents.ToList(); }
    }

    /// <summary>DB 기준값 스냅샷.</summary>
    public TokenBaseline Baseline
    {
        get { lock (_lock) return _baseline; }
    }

    /// <summary>마지막 DB 동기화 이후 TCP로 수신한 토큰 delta (컨텍스트별).</summary>
    public IReadOnlyList<SessionTokenSummary> TokenDelta
    {
        get { lock (_lock) return _tokenDelta.Values.OrderByDescending(s => s.LastActivity).ToList(); }
    }

    /// <summary>delta 전체 합산.</summary>
    public (long In, long Out, long Cached, int Requests) DeltaTotal
    {
        get
        {
            lock (_lock)
            {
                return (
                    _tokenDelta.Values.Sum(s => s.TotalIn),
                    _tokenDelta.Values.Sum(s => s.TotalOut),
                    _tokenDelta.Values.Sum(s => s.TotalCached),
                    _tokenDelta.Values.Sum(s => s.RequestCount));
            }
        }
    }

    public IReadOnlyList<ToolErrorEntry> ToolErrors
    {
        get { lock (_lock) return _toolErrors.ToList(); }
    }

    /// <summary>TCP에서 수신한 이벤트를 상태에 반영하고 Blazor 컴포넌트에 갱신 알림.</summary>
    public void Apply(LlmMonitorEvent evt)
    {
        lock (_lock)
        {
            switch (evt)
            {
                case ProcessingStartedEvent e:
                    _activeProcessing[e.ContextId] = new ProcessingState(e.ContextId, e.MessageCount, DateTimeOffset.UtcNow);
                    AddEvent($"[처리시작] {Short(e.ContextId)} msg={e.MessageCount}");
                    break;

                case ProcessingCompletedEvent e:
                    _activeProcessing.Remove(e.ContextId);
                    AddEvent($"[처리완료] {Short(e.ContextId)} {e.DurationMs}ms");
                    break;

                case TokenUsageEvent e:
                    var ctxId = e.ContextId ?? "";
                    if (!_tokenDelta.TryGetValue(ctxId, out var tok))
                    {
                        tok = new SessionTokenSummary { ContextId = ctxId };
                        _tokenDelta[ctxId] = tok;
                    }
                    tok.TotalIn     += e.TokensIn;
                    tok.TotalOut    += e.TokensOut;
                    tok.TotalCached += e.TokensCached;
                    tok.LastActivity = DateTimeOffset.UtcNow;
                    tok.RequestCount++;
                    CurrentModel = e.Model;
                    AddEvent($"[토큰] {Short(ctxId)} in={e.TokensIn:N0} out={e.TokensOut:N0} cached={e.TokensCached:N0}");
                    break;

                case ModelStatusEvent e:
                    CurrentModel = e.CurrentModel;
                    IsFallback   = true;
                    AddEvent($"[Fallback] {e.PreviousModel} → {e.CurrentModel}");
                    break;

                case ToolCallStartedEvent e:
                    AddEvent($"[툴시작] {e.ToolName} ({Short(e.ContextId ?? "")})");
                    break;

                case ToolCallCompletedEvent e:
                    AddEvent($"[툴완료] {e.ToolName} {e.DurationMs}ms");
                    break;

                case ToolCallFailedEvent e:
                    AddEvent($"[툴실패] {e.ToolName} {e.ErrorMessage}");
                    _toolErrors.AddFirst(new ToolErrorEntry(e.ToolName, e.ErrorMessage, e.ContextId, DateTimeOffset.UtcNow));
                    while (_toolErrors.Count > MaxToolErrors) _toolErrors.RemoveLast();
                    break;

                case CacheCreatedEvent e:
                    AddEvent($"[캐시생성] {ShortCacheName(e.CacheName)} {e.TokenCount:N0}tok");
                    break;

                case CacheExtendedEvent e:
                    AddEvent($"[캐시연장] {ShortCacheName(e.CacheName)} → {e.NewExpiresAt:HH:mm:ss}");
                    break;

                case CacheDeletedEvent e:
                    AddEvent($"[캐시삭제] {ShortCacheName(e.CacheName)} ({e.Reason})");
                    break;
            }
        }

        OnChange?.Invoke();
    }

    /// <summary>DB 동기화 완료 시 호출. baseline 갱신 + delta 리셋.</summary>
    public void SyncBaseline(long dbIn, long dbOut, long dbCached, int dbRequests)
    {
        lock (_lock)
        {
            _baseline = new TokenBaseline
            {
                TotalIn      = dbIn,
                TotalOut     = dbOut,
                TotalCached  = dbCached,
                RecordCount  = dbRequests,
                SyncedAt     = DateTimeOffset.UtcNow
            };
            // 새 baseline에 기존 delta 포함됨 → delta 리셋
            _tokenDelta.Clear();
        }
        OnChange?.Invoke();
    }

    /// <summary>연결 상태 변경 시 호출.</summary>
    public void SetConnected(bool connected)
    {
        lock (_lock)
        {
            IsConnected = connected;
            if (!connected) _activeProcessing.Clear();
        }
        OnChange?.Invoke();
    }

    private void AddEvent(string message)
    {
        _recentEvents.AddFirst(new RecentEvent(message, DateTimeOffset.UtcNow));
        while (_recentEvents.Count > MaxEventCount) _recentEvents.RemoveLast();
    }

    private static string Short(string id) =>
        id.Length > 12 ? id[..12] + "…" : id;

    private static string ShortCacheName(string name)
    {
        const string prefix = "cachedContents/";
        return name.StartsWith(prefix) ? name[prefix.Length..] : name;
    }
}

/// <summary>현재 처리 중인 Discord 메시지 배치 상태.</summary>
public record ProcessingState(string ContextId, int MessageCount, DateTimeOffset StartedAt);

/// <summary>최근 이벤트 로그 항목.</summary>
public record RecentEvent(string Message, DateTimeOffset Timestamp);

/// <summary>실시간 세션별 토큰 집계.</summary>
public class SessionTokenSummary
{
    public string ContextId { get; set; } = "";
    public long TotalIn { get; set; }
    public long TotalOut { get; set; }
    public long TotalCached { get; set; }
    public int RequestCount { get; set; }
    public DateTimeOffset LastActivity { get; set; }
}

/// <summary>실시간 tool 에러 항목.</summary>
public record ToolErrorEntry(string ToolName, string ErrorMessage, string? ContextId, DateTimeOffset Timestamp);

/// <summary>DB 동기화 시점의 전체 토큰 누적 스냅샷.</summary>
public class TokenBaseline
{
    public long TotalIn { get; set; }
    public long TotalOut { get; set; }
    public long TotalCached { get; set; }
    public int RecordCount { get; set; }
    /// <summary>동기화 시각. null이면 아직 동기화 미완료.</summary>
    public DateTimeOffset? SyncedAt { get; set; }
    public bool IsLoaded => SyncedAt.HasValue;
}
