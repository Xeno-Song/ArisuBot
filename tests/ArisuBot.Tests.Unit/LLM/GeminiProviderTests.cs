using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using Google.GenAI.Types;

namespace ArisuBot.Tests.Unit.LLM;

public class GeminiProviderTests
{
    // --- BuildSystemInstruction ---

    [Fact]
    public void BuildSystemInstruction_ReturnsNull_WhenNoSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "hello" }
        };

        var result = GeminiProvider.BuildSystemInstruction(messages);

        Assert.Null(result);
    }

    [Fact]
    public void BuildSystemInstruction_ReturnsSingleContent_WhenOneSystemMessage()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = "you are a bot" },
            new() { Role = Role.User, Content = "hello" }
        };

        var result = GeminiProvider.BuildSystemInstruction(messages);

        Assert.NotNull(result);
        Assert.Single(result.Parts!);
        Assert.Equal("you are a bot", result.Parts![0].Text);
    }

    [Fact]
    public void BuildSystemInstruction_JoinsMultipleSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = "line1" },
            new() { Role = Role.System, Content = "line2" }
        };

        var result = GeminiProvider.BuildSystemInstruction(messages);

        Assert.NotNull(result);
        Assert.Equal("line1\nline2", result.Parts![0].Text);
    }

    // --- BuildContents ---

    [Fact]
    public void BuildContents_ExcludesSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.System, Content = "sys" },
            new() { Role = Role.User, Content = "hello" },
            new() { Role = Role.Assistant, Content = "hi" }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.Parts!.Any(p => p.Text == "sys"));
    }

    [Fact]
    public void BuildContents_MapsUserRole_ToUserString()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "hello" }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Single(result);
        Assert.Equal("user", result[0].Role);
        Assert.Equal("hello", result[0].Parts![0].Text);
    }

    [Fact]
    public void BuildContents_MapsAssistantRole_ToModelString()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.Assistant, Content = "I am a bot" }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Single(result);
        Assert.Equal("model", result[0].Role);
        Assert.Equal("I am a bot", result[0].Parts![0].Text);
    }

    [Fact]
    public void BuildContents_PreservesOrder()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = "first" },
            new() { Role = Role.Assistant, Content = "second" },
            new() { Role = Role.User, Content = "third" }
        };

        var result = GeminiProvider.BuildContents(messages);

        Assert.Equal(3, result.Count);
        Assert.Equal("first", result[0].Parts![0].Text);
        Assert.Equal("second", result[1].Parts![0].Text);
        Assert.Equal("third", result[2].Parts![0].Text);
    }
}
