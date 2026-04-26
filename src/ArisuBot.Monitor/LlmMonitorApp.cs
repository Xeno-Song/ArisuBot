using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ArisuBot.LLM.Monitoring;

namespace ArisuBot.Monitor;

/// <summary>
/// LLM Monitor Sidecar 메인 로직.
/// TCP 클라이언트로 이벤트를 수신하고 콘솔 UI를 렌더링한다.
/// 서버 미연결 시 5초마다 재연결을 시도하며, 연결 중에는 이벤트를 실시간 반영한다.
/// </summary>
public class LlmMonitorApp
{
    private readonly string _host;
    private readonly int _port;

    // JSON 옵션: 서버 직렬화와 동일한 camelCase + 폴리모픽 역직렬화
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // 공유 상태 — _lock으로 보호
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _caches = new();
    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly LinkedList<string> _eventLog = new();
    private string _currentModel = "?";
    private bool _isFallback;
    private bool _isConnected;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;

    // 한 화면에 표시할 최대 세션 수 — 초과분은 TOTAL 행에만 합산
    private const int MaxDisplayedSessions = 10;

    /// <summary>Monitor Sidecar 앱. options에서 접속할 TCP 서버 주소를 읽는다.</summary>
    public LlmMonitorApp(MonitorServerOptions options)
    {
        _host = options.Host;
        _port = options.Port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.CursorVisible = false;

        // 렌더링 루프: 1초마다 콘솔 갱신 (TTL 카운트다운 표시)
        var renderTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Render();
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);

        // TCP 클라이언트 루프: 연결 → 수신 → 종료 시 재연결
        while (!ct.IsCancellationRequested)
        {
            await ConnectAndReadAsync(ct);

            // 연결 종료 후 짧은 대기 후 재시도
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }

        Console.CursorVisible = true;
        await renderTask;
    }

    /// <summary>서버에 TCP 연결해 이벤트를 수신한다. 연결 실패 또는 종료 시 반환.</summary>
    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        using var client = new TcpClient();

        try
        {
            // 5초 타임아웃 링크드 CTS — 서버 미실행 시 무한 대기 방지
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(_host, _port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return; // 5초 타임아웃 — 외부 루프에서 재시도
        }
        catch (OperationCanceledException)
        {
            return; // 사용자 취소
        }
        catch (SocketException)
        {
            return; // 서버 미실행 — 외부 루프에서 재시도
        }

        lock (_lock)
        {
            _isConnected = true;
            // 재연결 시 오래된 상태 초기화 — 서버는 신규 클라이언트 연결 시 상태 재전송 없음
            _caches.Clear();
            _sessions.Clear();
        }

        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ProcessEvent(line);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { return; }
        finally
        {
            lock (_lock) { _isConnected = false; }
        }
    }

    /// <summary>JSON 이벤트를 역직렬화해 상태를 갱신하고 이벤트 로그에 기록한다.</summary>
    private void ProcessEvent(string json)
    {
        LlmMonitorEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<LlmMonitorEvent>(json, _jsonOptions);
        }
        catch
        {
            return; // 역직렬화 실패 — 무시 (서버 버전 불일치 등)
        }

        if (evt is null) return;

        lock (_lock)
        {
            switch (evt)
            {
                case CacheCreatedEvent e:
                    _caches[e.CacheName] = new CacheEntry
                    {
                        CacheName  = e.CacheName,
                        TokenCount = e.TokenCount,
                        ExpiresAt  = e.ExpiresAt,
                        ContextId  = e.ContextId
                    };
                    AddEventLog($"CREATED  {ShortName(e.CacheName)}  ({Short(e.ContextId)})");
                    break;

                case CacheExtendedEvent e:
                    if (_caches.TryGetValue(e.CacheName, out var existing))
                        _caches[e.CacheName] = new CacheEntry
                        {
                            CacheName  = existing.CacheName,
                            TokenCount = existing.TokenCount,
                            ContextId  = existing.ContextId,
                            ExpiresAt  = e.NewExpiresAt
                        };
                    AddEventLog($"EXTENDED {ShortName(e.CacheName)}  → {e.NewExpiresAt:HH:mm:ss}");
                    break;

                case CacheDeletedEvent e:
                    _caches.Remove(e.CacheName);
                    AddEventLog($"DELETED  {ShortName(e.CacheName)}  ({e.Reason})");
                    break;

                case TokenUsageEvent e:
                    var ctxId = e.ContextId ?? "";
                    if (!_sessions.TryGetValue(ctxId, out var sess))
                    {
                        sess = new SessionState { ContextId = ctxId };
                        _sessions[ctxId] = sess;
                    }
                    sess.Apply(e);
                    _currentModel = e.Model;
                    AddEventLog($"TOKEN    {Short(ctxId)}  in={e.TokensIn:N0} cached={e.TokensCached:N0} out={e.TokensOut:N0}  [{e.Model}]");
                    break;

                case ModelStatusEvent e:
                    _currentModel = e.CurrentModel;
                    _isFallback   = true;
                    AddEventLog($"FALLBACK {e.PreviousModel} → {e.CurrentModel}");
                    break;
            }
        }
    }

    private void AddEventLog(string message)
    {
        _eventLog.AddFirst($"  {DateTimeOffset.Now:HH:mm:ss}  {message}");
        while (_eventLog.Count > 10)
            _eventLog.RemoveLast();
    }

    /// <summary>문자열을 maxLen 이하로 자른다. 초과 시 마지막 문자를 '…'으로 대체.</summary>
    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";

    /// <summary>캐시 이름에서 cachedContents/ 접두사 제거해 짧게 표시.</summary>
    private static string ShortName(string name)
    {
        const string prefix = "cachedContents/";
        return name.StartsWith(prefix) ? name[prefix.Length..] : name;
    }

    /// <summary>긴 ID를 12자로 자름.</summary>
    private static string Short(string id) =>
        id.Length > 12 ? id[..12] + "…" : id;

    /// <summary>콘솔 UI 전체 렌더링. Console.Clear() 후 재출력.</summary>
    private void Render()
    {
        List<CacheEntry> caches;
        List<SessionState> sessions;
        List<string> eventLog;
        string model;
        bool isFallback, isConnected;

        lock (_lock)
        {
            caches      = _caches.Values.OrderBy(c => c.CacheName).ToList();
            sessions    = _sessions.Values.OrderByDescending(s => s.LastActivity).ToList();
            eventLog    = _eventLog.ToList();
            model       = _currentModel;
            isFallback  = _isFallback;
            isConnected = _isConnected;
        }

        var sb = new StringBuilder();
        var connStatus = isConnected ? "[CONNECTED]" : "[DISCONNECTED — reconnecting...]";
        sb.AppendLine($"=== ArisuBot LLM Monitor ===  {connStatus}  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"Model: {model}  {(isFallback ? "[FALLBACK]" : "[MAIN]")}");
        sb.AppendLine();

        // Active Caches — 열 너비: NAME(28) TOKENS(8) TTL(10) CONTEXT(최대 13)
        // separator = 2+28+2+8+2+10+2+13 = 67
        const int cacheNameW  = 28;
        const int cacheTokenW = 8;
        const int cacheTtlW   = 10;
        const int cacheSepW   = 2 + cacheNameW + 2 + cacheTokenW + 2 + cacheTtlW + 2 + 13;
        sb.AppendLine($"Active Caches ({caches.Count})");
        sb.AppendLine(new string('─', cacheSepW));
        sb.AppendLine($"  {"NAME",-cacheNameW}  {"TOKENS",cacheTokenW}  {"TTL",cacheTtlW}  CONTEXT");
        foreach (var c in caches)
        {
            var ttl = c.TimeToLive;
            var ttlStr = ttl < TimeSpan.Zero
                ? "expired"
                : $"{(int)ttl.TotalMinutes}m {ttl.Seconds:D2}s";
            sb.AppendLine($"  {Truncate(ShortName(c.CacheName), cacheNameW),-cacheNameW}  {c.TokenCount,cacheTokenW:N0}  {ttlStr,cacheTtlW}  {Short(c.ContextId)}");
        }
        sb.AppendLine(new string('─', cacheSepW));
        sb.AppendLine();

        // Active Sessions — 3개 섹션: SESSION SIZE | LATEST REQUEST | ACCUMULATED
        // 열 너비 정의
        const int sessCtxW  = 16; // CONTEXT
        const int sessSzW   = 9;  // SESSION SIZE
        const int sessLInW  = 9;  // LATEST IN
        const int sessLCaW  = 9;  // LATEST CACHED
        const int sessLOutW = 7;  // LATEST OUT
        const int sessTInW  = 9;  // TOTAL IN
        const int sessTCaW  = 9;  // TOTAL CACHED
        const int sessTOutW = 7;  // TOTAL OUT
        // separator = 2+ctx+2+sz+3+lIn+2+lCa+2+lOut+3+tIn+2+tCa+2+tOut
        //           = 2+16+2+9+3+9+2+9+2+7+3+9+2+9+2+7 = 93
        const int sessSepW = 2 + sessCtxW + 2 + sessSzW + 3 + sessLInW + 2 + sessLCaW + 2 + sessLOutW
                           + 3 + sessTInW + 2 + sessTCaW + 2 + sessTOutW;

        // 전체 세션 합계
        var allTotalIn     = sessions.Sum(s => s.TotalIn);
        var allTotalOut    = sessions.Sum(s => s.TotalOut);
        var allTotalCached = sessions.Sum(s => s.TotalCached);

        var sessTitle = sessions.Count > MaxDisplayedSessions
            ? $"Active Sessions (최근 {MaxDisplayedSessions} / 전체 {sessions.Count})"
            : $"Active Sessions ({sessions.Count})";
        sb.AppendLine(sessTitle);
        sb.AppendLine(new string('─', sessSepW));
        // 섹션 헤더 — │ 구분자로 3개 그룹 시각화
        sb.AppendLine($"  {"CONTEXT",-sessCtxW}  {"SIZE",sessSzW} │ {"LATEST: IN",sessLInW}  {"CACHED",sessLCaW}  {"OUT",sessLOutW} │ {"TOTAL: IN",sessTInW}  {"CACHED",sessTCaW}  {"OUT",sessTOutW}");
        sb.AppendLine(new string('─', sessSepW));
        foreach (var s in sessions.Take(MaxDisplayedSessions))
        {
            sb.AppendLine(
                $"  {Truncate(Short(s.ContextId), sessCtxW),-sessCtxW}" +
                $"  {s.CurrentSessionSize,sessSzW:N0}" +
                $" │ {s.LastIn,sessLInW:N0}  {s.LastCached,sessLCaW:N0}  {s.LastOut,sessLOutW:N0}" +
                $" │ {s.TotalIn,sessTInW:N0}  {s.TotalCached,sessTCaW:N0}  {s.TotalOut,sessTOutW:N0}");
        }
        sb.AppendLine(new string('─', sessSepW));
        // TOTAL 행 — SESSION SIZE + LATEST 칸은 합산 의미 없으므로 "-" 표시
        sb.AppendLine(
            $"  {"TOTAL",-sessCtxW}" +
            $"  {"-",sessSzW}" +
            $" │ {"-",sessLInW}  {"-",sessLCaW}  {"-",sessLOutW}" +
            $" │ {allTotalIn,sessTInW:N0}  {allTotalCached,sessTCaW:N0}  {allTotalOut,sessTOutW:N0}");
        sb.AppendLine(new string('─', sessSepW));
        sb.AppendLine();

        // Events
        sb.AppendLine("Events (last 10)");
        foreach (var e in eventLog)
            sb.AppendLine(e);

        Console.Clear();
        Console.Write(sb.ToString());
    }
}
