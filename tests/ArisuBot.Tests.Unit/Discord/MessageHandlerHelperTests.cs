using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;

namespace ArisuBot.Tests.Unit.Discord;

/// <summary>MessageHandler의 internal static 헬퍼 메서드 테스트.</summary>
public class MessageHandlerHelperTests
{
    // --- ShouldRespond (기존 테스트와 동일, 파일 분리) ---

    [Fact]
    public void ShouldRespond_ReturnsTrue_WhenChannelInList()
    {
        var options = new MessageListenerOptions { ChannelIds = [111UL, 222UL], RespondToMentions = false };

        Assert.True(MessageHandler.ShouldRespond(options, 111UL, isMention: false));
    }

    [Fact]
    public void ShouldRespond_ReturnsFalse_WhenChannelNotInListAndNoMention()
    {
        var options = new MessageListenerOptions { ChannelIds = [111UL], RespondToMentions = true };

        Assert.False(MessageHandler.ShouldRespond(options, 999UL, isMention: false));
    }

    [Fact]
    public void ShouldRespond_ReturnsTrue_WhenMentionAndRespondToMentionsEnabled()
    {
        var options = new MessageListenerOptions { ChannelIds = [], RespondToMentions = true };

        Assert.True(MessageHandler.ShouldRespond(options, 999UL, isMention: true));
    }

    [Fact]
    public void ShouldRespond_ReturnsFalse_WhenMentionButRespondToMentionsDisabled()
    {
        var options = new MessageListenerOptions { ChannelIds = [], RespondToMentions = false };

        Assert.False(MessageHandler.ShouldRespond(options, 999UL, isMention: true));
    }

    // --- SplitIntoChunks ---

    [Fact]
    public void SplitIntoChunks_ReturnsEmpty_ForEmptyString()
    {
        Assert.Empty(MessageHandler.SplitIntoChunks(string.Empty));
    }

    [Fact]
    public void SplitIntoChunks_ReturnsSingleChunk_WhenUnderLimit()
    {
        var text = new string('a', 1999);

        var chunks = MessageHandler.SplitIntoChunks(text, 2000).ToList();

        Assert.Single(chunks);
        Assert.Equal(1999, chunks[0].Length);
    }

    [Fact]
    public void SplitIntoChunks_SplitsAtExactLimit()
    {
        var text = new string('a', 2000);

        var chunks = MessageHandler.SplitIntoChunks(text, 2000).ToList();

        Assert.Single(chunks);
        Assert.Equal(2000, chunks[0].Length);
    }

    [Fact]
    public void SplitIntoChunks_SplitsIntoMultipleChunks_WhenOverLimit()
    {
        var text = new string('a', 4500);

        var chunks = MessageHandler.SplitIntoChunks(text, 2000).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(2000, chunks[0].Length);
        Assert.Equal(2000, chunks[1].Length);
        Assert.Equal(500, chunks[2].Length);
    }

    [Fact]
    public void SplitIntoChunks_PreservesContent()
    {
        var text = "hello world";

        var chunks = MessageHandler.SplitIntoChunks(text, 2000).ToList();

        Assert.Equal(text, string.Concat(chunks));
    }

    // --- ReplaceMentions ---

    [Fact]
    public void ReplaceMentions_ReplacesKnownName_WithDiscordMention()
    {
        var participants = new Dictionary<string, ulong> { ["Xeno"] = 123456789UL };

        var result = MessageHandler.ReplaceMentions("안녕 <<Xeno>>!", participants);

        Assert.Equal("안녕 <@123456789>!", result);
    }

    [Fact]
    public void ReplaceMentions_KeepsUnknownName_Unchanged()
    {
        var participants = new Dictionary<string, ulong>();

        var result = MessageHandler.ReplaceMentions("안녕 <<Unknown>>!", participants);

        Assert.Equal("안녕 <<Unknown>>!", result);
    }

    [Fact]
    public void ReplaceMentions_HandlesMultipleMatches()
    {
        var participants = new Dictionary<string, ulong> { ["A"] = 1UL, ["B"] = 2UL };

        var result = MessageHandler.ReplaceMentions("<<A>> and <<B>>", participants);

        Assert.Equal("<@1> and <@2>", result);
    }

    [Fact]
    public void ReplaceMentions_NoPatternInText_ReturnsOriginal()
    {
        var participants = new Dictionary<string, ulong> { ["Xeno"] = 123UL };

        var result = MessageHandler.ReplaceMentions("아무 멘션 없음", participants);

        Assert.Equal("아무 멘션 없음", result);
    }

    [Fact]
    public void ReplaceMentions_MixedKnownAndUnknown()
    {
        var participants = new Dictionary<string, ulong> { ["Xeno"] = 999UL };

        var result = MessageHandler.ReplaceMentions("<<Xeno>> <<Ghost>>", participants);

        Assert.Equal("<@999> <<Ghost>>", result);
    }
}
