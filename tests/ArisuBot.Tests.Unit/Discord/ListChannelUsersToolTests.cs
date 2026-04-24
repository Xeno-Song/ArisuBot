using ArisuBot.Discord.Tools;
using Microsoft.Extensions.Logging;
using Moq;

namespace ArisuBot.Tests.Unit.Discord;

public class ListChannelUsersToolTests
{
    // --- Name / Definition ---

    [Fact]
    public void Name_ReturnsDiscordListChannelUsers()
    {
        var tool = CreateStubTool();
        Assert.Equal("discord_list_channel_users", tool.Name);
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
        // 전역 규칙 2.1: channelId 파라미터가 스키마에 없음을 확인
        var doc = System.Text.Json.JsonDocument.Parse(tool.Definition.ParametersJsonSchema);
        Assert.NotNull(doc);
        var props = doc.RootElement.GetProperty("properties");
        Assert.False(props.TryGetProperty("channelId", out _), "channelId should not be in schema (global rule 2.1)");
    }

    private static ListChannelUsersTool CreateStubTool() =>
        new(null!, new Mock<ILogger<ListChannelUsersTool>>().Object);
}
