using ArisuBot.Discord.Handlers;
using ArisuBot.Discord.Options;

namespace ArisuBot.Tests.Unit.Discord;

public class MessageHandlerTests
{
    [Fact]
    public void ShouldRespond_ReturnsTrue_WhenChannelInList()
    {
        var options = new MessageListenerOptions
        {
            ChannelIds = [111UL, 222UL],
            RespondToMentions = false
        };

        Assert.True(MessageHandler.ShouldRespond(options, 111UL, isMention: false));
    }

    [Fact]
    public void ShouldRespond_ReturnsFalse_WhenChannelNotInListAndNoMention()
    {
        var options = new MessageListenerOptions
        {
            ChannelIds = [111UL],
            RespondToMentions = true
        };

        Assert.False(MessageHandler.ShouldRespond(options, 999UL, isMention: false));
    }

    [Fact]
    public void ShouldRespond_ReturnsTrue_WhenMentionAndRespondToMentionsEnabled()
    {
        var options = new MessageListenerOptions
        {
            ChannelIds = [],
            RespondToMentions = true
        };

        Assert.True(MessageHandler.ShouldRespond(options, 999UL, isMention: true));
    }

    [Fact]
    public void ShouldRespond_ReturnsFalse_WhenMentionButRespondToMentionsDisabled()
    {
        var options = new MessageListenerOptions
        {
            ChannelIds = [],
            RespondToMentions = false
        };

        Assert.False(MessageHandler.ShouldRespond(options, 999UL, isMention: true));
    }
}
