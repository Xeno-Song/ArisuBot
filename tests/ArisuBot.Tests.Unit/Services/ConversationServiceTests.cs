using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Services;

public class ConversationServiceTests
{
    private readonly Mock<IConversationRepository> _repoMock;
    private readonly ConversationService _sut;
    private const int UserMax = 5;
    private const int ChannelMax = 10;

    public ConversationServiceTests()
    {
        _repoMock = new Mock<IConversationRepository>();
        var options = Options.Create(new MemoryOptions
        {
            UserContextMaxMessages = UserMax,
            ChannelContextMaxMessages = ChannelMax
        });
        _sut = new ConversationService(_repoMock.Object, options);
    }

    [Fact]
    public void BuildMessageList_SystemPromptFirst()
    {
        var context = new ConversationContext { Type = ContextType.User };

        var messages = _sut.BuildMessageList(context, "hello", "you are a bot");

        Assert.Equal(Role.System, messages[0].Role);
        Assert.Equal("you are a bot", messages[0].Content);
    }

    [Fact]
    public void BuildMessageList_IncludesHistoryAndNewMessage()
    {
        var context = new ConversationContext
        {
            Type = ContextType.User,
            Messages = [new() { Role = Role.User, Content = "prev" }]
        };

        var messages = _sut.BuildMessageList(context, "new", "sys");

        Assert.Contains(messages, m => m.Content == "prev");
        Assert.Contains(messages, m => m.Content == "new" && m.Role == Role.User);
    }

    [Fact]
    public void BuildMessageList_TrimsOldMessages_WhenExceedingUserMax()
    {
        var history = Enumerable.Range(0, UserMax + 3)
            .Select(i => new ChatMessage { Role = Role.User, Content = $"msg{i}" })
            .ToList();
        var context = new ConversationContext { Type = ContextType.User, Messages = history };

        var messages = _sut.BuildMessageList(context, "new", "sys");

        // system(1) + UserMax history + new user message(1)
        Assert.Equal(UserMax + 2, messages.Count);
    }

    [Fact]
    public void BuildMessageList_UsesChannelMax_ForChannelContext()
    {
        var history = Enumerable.Range(0, ChannelMax + 2)
            .Select(i => new ChatMessage { Role = Role.User, Content = $"msg{i}" })
            .ToList();
        var context = new ConversationContext { Type = ContextType.Channel, Messages = history };

        var messages = _sut.BuildMessageList(context, "new", "sys");

        Assert.Equal(ChannelMax + 2, messages.Count);
    }

    [Fact]
    public async Task AppendMessage_AddsMessageAndCallsSaveContext()
    {
        var context = new ConversationContext { Type = ContextType.User };
        var message = new ChatMessage { Role = Role.User, Content = "hi" };

        await _sut.AppendMessageAsync(context, message);

        Assert.Contains(message, context.Messages);
        _repoMock.Verify(r => r.SaveContextAsync(context, default), Times.Once);
    }

    [Fact]
    public async Task GetContext_User_CallsGetUserContextAsync()
    {
        _repoMock.Setup(r => r.GetUserContextAsync(1ul, default))
                 .ReturnsAsync(new ConversationContext());

        await _sut.GetContextAsync(1ul, ContextType.User);

        _repoMock.Verify(r => r.GetUserContextAsync(1ul, default), Times.Once);
        _repoMock.Verify(r => r.GetChannelContextAsync(It.IsAny<ulong>(), default), Times.Never);
    }

    [Fact]
    public async Task GetContext_Channel_CallsGetChannelContextAsync()
    {
        _repoMock.Setup(r => r.GetChannelContextAsync(2ul, default))
                 .ReturnsAsync(new ConversationContext());

        await _sut.GetContextAsync(2ul, ContextType.Channel);

        _repoMock.Verify(r => r.GetChannelContextAsync(2ul, default), Times.Once);
        _repoMock.Verify(r => r.GetUserContextAsync(It.IsAny<ulong>(), default), Times.Never);
    }

    [Fact]
    public async Task AppendTokenUsageAsync_AddsUsageAndCallsSaveContext()
    {
        var context = new ConversationContext { Type = ContextType.Channel };
        var usage = new TokenUsage { TokensIn = 100, TokensOut = 50, TokensCachedIn = 10 };

        await _sut.AppendTokenUsageAsync(context, usage);

        Assert.Contains(usage, context.TokenUsage);
        _repoMock.Verify(r => r.SaveContextAsync(context, default), Times.Once);
    }

    [Fact]
    public async Task StartNewSessionAsync_CallsCreateNewSessionAsync_WithCorrectArgs()
    {
        var newContext = new ConversationContext { TargetId = 10ul, Type = ContextType.Channel };
        _repoMock.Setup(r => r.CreateNewSessionAsync(10ul, ContextType.Channel, default))
                 .ReturnsAsync(newContext);

        var result = await _sut.StartNewSessionAsync(10ul, ContextType.Channel);

        Assert.Equal(newContext, result);
        _repoMock.Verify(r => r.CreateNewSessionAsync(10ul, ContextType.Channel, default), Times.Once);
    }
}
