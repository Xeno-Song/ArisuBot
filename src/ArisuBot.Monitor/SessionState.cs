using ArisuBot.LLM.Monitoring;

namespace ArisuBot.Monitor;

/// <summary>특정 대화 컨텍스트(MongoDB doc ID)의 토큰 사용량 — 최근 요청 스냅샷 + 누적 합산 보유.</summary>
public class SessionState
{
    public string ContextId { get; init; } = string.Empty;

    // ── 누적 합산 ──────────────────────────────────────────────────────────
    public int TotalIn     { get; private set; }
    public int TotalOut    { get; private set; }
    public int TotalCached { get; private set; }

    // ── 최근 요청 스냅샷 (Apply 호출마다 덮어씀) ──────────────────────────
    public int LastIn     { get; private set; }
    public int LastOut    { get; private set; }
    public int LastCached { get; private set; }

    public DateTimeOffset LastActivity { get; private set; }

    /// <summary>현재 컨텍스트 창 추정 크기 = 최근 입력 + 최근 출력.</summary>
    public int CurrentSessionSize => LastIn + LastOut;

    /// <summary>누적 기준 캐시 적중률 (%). TotalIn이 0이면 0.</summary>
    public double CachePercent =>
        TotalIn > 0 ? (double)TotalCached / TotalIn * 100.0 : 0.0;

    /// <summary>TOKEN_USAGE 이벤트를 반영한다. 누적 필드는 +=, 최근 스냅샷 필드는 = 로 갱신.</summary>
    public void Apply(TokenUsageEvent evt)
    {
        TotalIn      += evt.TokensIn;
        TotalOut     += evt.TokensOut;
        TotalCached  += evt.TokensCached;

        LastIn        = evt.TokensIn;
        LastOut       = evt.TokensOut;
        LastCached    = evt.TokensCached;

        LastActivity  = evt.Timestamp;
    }
}
