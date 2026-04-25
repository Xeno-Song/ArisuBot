using ArisuBot.LLM.Monitoring;
using ArisuBot.Monitor;

namespace ArisuBot.Tests.Unit.Monitor;

/// <summary>SessionState 누적 로직 단위 테스트.</summary>
public class SessionStateTests
{
    [Fact]
    public void Apply_AccumulatesTokens_AcrossMultipleEvents()
    {
        var state = new SessionState { ContextId = "ctx-1" };

        state.Apply(new TokenUsageEvent("ctx-1", TokensIn: 100, TokensOut: 40, TokensCached: 30, Model: "m"));
        state.Apply(new TokenUsageEvent("ctx-1", TokensIn: 200, TokensOut: 60, TokensCached: 50, Model: "m"));

        Assert.Equal(300, state.TotalIn);
        Assert.Equal(100, state.TotalOut);
        Assert.Equal(80,  state.TotalCached);
    }

    [Fact]
    public void Apply_UpdatesLastActivity_ToEventTimestamp()
    {
        var state = new SessionState { ContextId = "ctx-2" };
        var evt = new TokenUsageEvent("ctx-2", 10, 5, 0, "m");

        state.Apply(evt);

        Assert.Equal(evt.Timestamp, state.LastActivity);
    }

    [Fact]
    public void CachePercent_ReturnsCorrectRatio()
    {
        var state = new SessionState { ContextId = "ctx-3" };
        state.Apply(new TokenUsageEvent("ctx-3", TokensIn: 200, TokensOut: 50, TokensCached: 100, Model: "m"));

        // 100 / 200 * 100 = 50.0%
        Assert.Equal(50.0, state.CachePercent, precision: 5);
    }

    [Fact]
    public void CachePercent_IsZero_WhenNoTokensIn()
    {
        var state = new SessionState { ContextId = "ctx-4" };

        // Apply 없음 — TotalIn = 0
        Assert.Equal(0.0, state.CachePercent);
    }

    [Fact]
    public void CachePercent_IsZero_WhenNoCachedTokens()
    {
        var state = new SessionState { ContextId = "ctx-5" };
        state.Apply(new TokenUsageEvent("ctx-5", TokensIn: 100, TokensOut: 30, TokensCached: 0, Model: "m"));

        Assert.Equal(0.0, state.CachePercent);
    }

    // ── Last 필드 (최근 요청 스냅샷) ──────────────────────────────────────

    [Fact]
    public void Apply_SetsLastFields_WithMostRecentValues()
    {
        // 2회 Apply 후 Last 필드는 두 번째 값만 반영 (덮어쓰기)
        var state = new SessionState { ContextId = "ctx-6" };

        state.Apply(new TokenUsageEvent("ctx-6", TokensIn: 100, TokensOut: 40, TokensCached: 30, Model: "m"));
        state.Apply(new TokenUsageEvent("ctx-6", TokensIn: 200, TokensOut: 60, TokensCached: 50, Model: "m"));

        Assert.Equal(200, state.LastIn);
        Assert.Equal(60,  state.LastOut);
        Assert.Equal(50,  state.LastCached);
    }

    [Fact]
    public void Apply_LastFields_DoNotAffectTotals()
    {
        // Last 필드 추가로 TotalIn/Out/Cached 누적 동작 변경 없음
        var state = new SessionState { ContextId = "ctx-7" };

        state.Apply(new TokenUsageEvent("ctx-7", TokensIn: 100, TokensOut: 40, TokensCached: 30, Model: "m"));
        state.Apply(new TokenUsageEvent("ctx-7", TokensIn: 200, TokensOut: 60, TokensCached: 50, Model: "m"));

        Assert.Equal(300, state.TotalIn);
        Assert.Equal(100, state.TotalOut);
        Assert.Equal(80,  state.TotalCached);
    }

    [Fact]
    public void CurrentSessionSize_IsLastInPlusLastOut()
    {
        var state = new SessionState { ContextId = "ctx-8" };
        state.Apply(new TokenUsageEvent("ctx-8", TokensIn: 1200, TokensOut: 350, TokensCached: 0, Model: "m"));

        Assert.Equal(1550, state.CurrentSessionSize);
    }

    [Fact]
    public void CurrentSessionSize_IsZero_BeforeFirstApply()
    {
        var state = new SessionState { ContextId = "ctx-9" };

        Assert.Equal(0, state.CurrentSessionSize);
    }
}
