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

        var summaries = _sut.TokenSummaries;
        Assert.Single(summaries);
        Assert.Equal(300, summaries[0].TotalIn);
        Assert.Equal(130, summaries[0].TotalOut);
        Assert.Equal(30, summaries[0].TotalCached);
        Assert.Equal(2, summaries[0].RequestCount);
        Assert.Equal("gemini-pro", _sut.CurrentModel);
    }

    [Fact]
    public void Apply_TokenUsageEvent_DifferentContextIds_CreateSeparateSummaries()
    {
        _sut.Apply(new TokenUsageEvent("ctx-1", 100, 50, 0, "m1"));
        _sut.Apply(new TokenUsageEvent("ctx-2", 200, 80, 0, "m1"));

        Assert.Equal(2, _sut.TokenSummaries.Count);
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
