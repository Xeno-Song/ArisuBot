using ArisuBot.Core.Models;
using ArisuBot.Discord.Options;
using ArisuBot.Discord.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Discord;

public class TimeoutUserToolTests
{
    private static DiscordToolOptions MakeOptions(int min = 60, int max = 604800) =>
        new() { TimeoutMinSeconds = min, TimeoutMaxSeconds = max };

    // --- ValidateTimeoutParameters ---

    [Fact]
    public void ValidateTimeoutParameters_ReturnsNull_WhenValidArgs()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 300 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out var userName, out var duration);

        Assert.Null(error);
        Assert.Equal("Xeno", userName);
        Assert.Equal(300, duration);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenUserNameMissing()
    {
        var args = new Dictionary<string, object> { ["durationSeconds"] = 300 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenUserNameValueIsNull()
    {
        // 키는 있지만 값이 null인 경우
        var args = new Dictionary<string, object> { ["userName"] = null!, ["durationSeconds"] = 300 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenUserNameEmpty()
    {
        // 빈 문자열은 유효하지 않은 displayName
        var args = new Dictionary<string, object> { ["userName"] = "", ["durationSeconds"] = 300 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenUserNameWhitespace()
    {
        var args = new Dictionary<string, object> { ["userName"] = "   ", ["durationSeconds"] = 300 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenDurationValueIsNull()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = null! };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ReturnsError_WhenDurationMissing()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno" };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out _);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_ClampsToMin_WhenDurationBelowMin()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 10 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(min: 60), out _, out var duration);

        Assert.Null(error);
        Assert.Equal(60, duration);
    }

    [Fact]
    public void ValidateTimeoutParameters_ClampsToMax_WhenDurationAboveMax()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 9999999 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(max: 604800), out _, out var duration);

        Assert.Null(error);
        Assert.Equal(604800, duration);
    }

    [Fact]
    public void ValidateTimeoutParameters_AcceptsLong_AsDuration()
    {
        // Gemini SDK가 integer를 long으로 전달하는 경우 대응
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 300L };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out var duration);

        Assert.Null(error);
        Assert.Equal(300, duration);
    }

    [Fact]
    public void ValidateTimeoutParameters_AcceptsDouble_AsDuration()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 300.0 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(), out _, out var duration);

        Assert.Null(error);
        Assert.Equal(300, duration);
    }

    [Fact]
    public void ValidateTimeoutParameters_AcceptsMinBoundary()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 60 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(min: 60), out _, out _);

        Assert.Null(error);
    }

    [Fact]
    public void ValidateTimeoutParameters_AcceptsMaxBoundary()
    {
        var args = new Dictionary<string, object> { ["userName"] = "Xeno", ["durationSeconds"] = 604800 };
        var error = TimeoutUserTool.ValidateTimeoutParameters(args, MakeOptions(max: 604800), out _, out _);

        Assert.Null(error);
    }

    // --- Name / Definition ---

    [Fact]
    public void Name_ReturnsDiscordTimeoutUser()
    {
        var tool = CreateStubTool();
        Assert.Equal("discord_timeout_user", tool.Name);
    }

    [Fact]
    public void Definition_HasMatchingName()
    {
        var tool = CreateStubTool();
        Assert.Equal(tool.Name, tool.Definition.Name);
    }

    [Fact]
    public void Definition_HasNonEmptyDescription()
    {
        var tool = CreateStubTool();
        Assert.NotEmpty(tool.Definition.Description);
    }

    [Fact]
    public void Definition_HasValidParametersJsonSchema()
    {
        var tool = CreateStubTool();
        Assert.NotEmpty(tool.Definition.ParametersJsonSchema);
        // 유효한 JSON이며 userName이 required 필드로 포함되는지 확인
        var doc = System.Text.Json.JsonDocument.Parse(tool.Definition.ParametersJsonSchema);
        Assert.NotNull(doc);
        var required = doc.RootElement.GetProperty("required");
        Assert.Contains("userName", required.EnumerateArray().Select(e => e.GetString()));
    }

    // DiscordSocketClient를 생성할 수 없으므로 null로 전달 (Discord API 미호출 테스트에서만 사용)
    private static TimeoutUserTool CreateStubTool(int min = 60, int max = 604800) =>
        new(null!, Options.Create(new DiscordToolOptions { TimeoutMinSeconds = min, TimeoutMaxSeconds = max }),
            new Mock<ILogger<TimeoutUserTool>>().Object);
}
