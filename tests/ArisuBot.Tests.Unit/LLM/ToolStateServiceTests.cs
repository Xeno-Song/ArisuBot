using ArisuBot.LLM.Services;

namespace ArisuBot.Tests.Unit.LLM;

/// <summary>ToolStateService의 기본 동작 및 상태 변경 검증.</summary>
public class ToolStateServiceTests
{
    private readonly ToolStateService _sut = new();

    [Fact]
    public void IsEnabled_UnregisteredTool_ReturnsTrue()
    {
        // 등록되지 않은 tool은 기본값 true(활성) 반환
        Assert.True(_sut.IsEnabled("some_tool"));
    }

    [Fact]
    public void SetEnabled_Disable_ThenIsEnabled_ReturnsFalse()
    {
        _sut.SetEnabled("my_tool", false);

        Assert.False(_sut.IsEnabled("my_tool"));
    }

    [Fact]
    public void SetEnabled_DisableThenEnable_ReturnsTrue()
    {
        _sut.SetEnabled("my_tool", false);
        _sut.SetEnabled("my_tool", true);

        Assert.True(_sut.IsEnabled("my_tool"));
    }

    [Fact]
    public void GetAll_ReturnsOnlyExplicitlySetTools()
    {
        _sut.SetEnabled("tool_a", true);
        _sut.SetEnabled("tool_b", false);

        var all = _sut.GetAll();

        Assert.Equal(2, all.Count);
        Assert.True(all["tool_a"]);
        Assert.False(all["tool_b"]);
    }

    [Fact]
    public void GetAll_EmptyInitially()
    {
        var all = _sut.GetAll();
        Assert.Empty(all);
    }

    [Fact]
    public void IndependentTools_StateNotAffected()
    {
        _sut.SetEnabled("tool_a", false);

        // tool_b는 별도로 등록 — tool_a의 상태에 영향 없음
        Assert.True(_sut.IsEnabled("tool_b"));
        Assert.False(_sut.IsEnabled("tool_a"));
    }
}
