using ArisuBot.Dashboard.Services;
using ArisuBot.LLM.Monitoring;

namespace ArisuBot.Tests.Unit.Dashboard;

/// <summary>DashboardStateService가 각 이벤트 타입에 따라 상태를 올바르게 업데이트하는지 검증.</summary>
public class DashboardStateServiceTests
{
    private readonly DashboardStateService _sut = new();

    [Fact]
    public void Apply_ProcessingStarted_AddsActiveProcessing_AndFiresOnChange()
    {
        var fired = false;
        _sut.OnChange += () => fired = true;

        _sut.Apply(new ProcessingStartedEvent("ctx-1", 3));

        Assert.True(fired);
        Assert.True(_sut.IsProcessing);
        Assert.Single(_sut.ActiveProcessing);
        Assert.Equal("ctx-1", _sut.ActiveProcessing[0].ContextId);
        Assert.Equal(3, _sut.ActiveProcessing[0].MessageCount);
    }

    [Fact]
    public void Apply_ProcessingCompleted_RemovesActiveProcessing()
    {
        _sut.Apply(new ProcessingStartedEvent("ctx-1", 2));
        _sut.Apply(new ProcessingCompletedEvent("ctx-1", 500));

        Assert.False(_sut.IsProcessing);
        Assert.Empty(_sut.ActiveProcessing);
    }

    [Fact]
    public void Apply_TokenUsageEvent_AccumulatesTokensAndSetsModel()
    {
        _sut.Apply(new TokenUsageEvent("ctx-1", TokensIn: 100, TokensOut: 50, TokensCached: 10, Model: "gemini-pro"));
        _sut.Apply(new TokenUsageEvent("ctx-1", TokensIn: 200, TokensOut: 80, TokensCached: 20, Model: "gemini-pro"));

        var delta = _sut.TokenDelta;
        Assert.Single(delta);
        Assert.Equal(300, delta[0].TotalIn);
        Assert.Equal(130, delta[0].TotalOut);
        Assert.Equal(30, delta[0].TotalCached);
        Assert.Equal(2, delta[0].RequestCount);
        Assert.Equal("gemini-pro", _sut.CurrentModel);
    }

    [Fact]
    public void Apply_TokenUsageEvent_DifferentContextIds_CreateSeparateSummaries()
    {
        _sut.Apply(new TokenUsageEvent("ctx-1", 100, 50, 0, "m1"));
        _sut.Apply(new TokenUsageEvent("ctx-2", 200, 80, 0, "m1"));

        Assert.Equal(2, _sut.TokenDelta.Count);
    }

    [Fact]
    public void SyncBaseline_UpdatesBaseline_AndClearsDelta()
    {
        // delta 먼저 쌓기
        _sut.Apply(new TokenUsageEvent("ctx-1", 100, 50, 10, "m1"));
        Assert.Single(_sut.TokenDelta);

        // baseline sync
        _sut.SyncBaseline(1000, 500, 100, 10);

        var b = _sut.Baseline;
        Assert.True(b.IsLoaded);
        Assert.Equal(1000, b.TotalIn);
        Assert.Equal(500, b.TotalOut);
        Assert.Equal(100, b.TotalCached);
        Assert.Equal(10, b.RecordCount);
        // delta 리셋 확인
        Assert.Empty(_sut.TokenDelta);
    }

    [Fact]
    public void SyncBaseline_FiresOnChange()
    {
        var fired = false;
        _sut.OnChange += () => fired = true;

        _sut.SyncBaseline(100, 50, 10, 1);

        Assert.True(fired);
    }

    [Fact]
    public void DeltaTotal_SumsAcrossContexts()
    {
        _sut.Apply(new TokenUsageEvent("ctx-1", 100, 50, 10, "m1"));
        _sut.Apply(new TokenUsageEvent("ctx-2", 200, 80, 20, "m1"));

        var (dIn, dOut, dCached, dReq) = _sut.DeltaTotal;
        Assert.Equal(300, dIn);
        Assert.Equal(130, dOut);
        Assert.Equal(30, dCached);
        Assert.Equal(2, dReq);
    }

    [Fact]
    public void Apply_ToolCallFailedEvent_AddsToToolErrors()
    {
        _sut.Apply(new ToolCallFailedEvent("ctx-1", "bad_tool", "tool failed", 100));

        Assert.Single(_sut.ToolErrors);
        Assert.Equal("bad_tool", _sut.ToolErrors[0].ToolName);
        Assert.Equal("tool failed", _sut.ToolErrors[0].ErrorMessage);
    }

    [Fact]
    public void Apply_ModelStatusEvent_UpdatesModelAndFallback()
    {
        _sut.Apply(new ModelStatusEvent("fallback-model", "primary-model"));

        Assert.Equal("fallback-model", _sut.CurrentModel);
        Assert.True(_sut.IsFallback);
    }

    [Fact]
    public void Apply_AddsToRecentEvents()
    {
        _sut.Apply(new ProcessingStartedEvent("ctx-1", 1));
        _sut.Apply(new ProcessingCompletedEvent("ctx-1", 100));

        Assert.Equal(2, _sut.RecentEvents.Count);
    }

    [Fact]
    public void SetConnected_False_ClearsActiveProcessing()
    {
        _sut.Apply(new ProcessingStartedEvent("ctx-1", 1));
        Assert.True(_sut.IsProcessing);

        _sut.SetConnected(false);

        Assert.False(_sut.IsConnected);
        Assert.False(_sut.IsProcessing);
    }

    [Fact]
    public void SetConnected_True_SetsConnectedFlag()
    {
        _sut.SetConnected(true);
        Assert.True(_sut.IsConnected);
    }
}
