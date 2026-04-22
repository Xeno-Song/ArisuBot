using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB;
using ArisuBot.Infrastructure.Options;
using EphemeralMongo;
using Microsoft.Extensions.Options;

namespace ArisuBot.Tests.Integration.MongoDB;

public class ConversationRepositoryTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private ConversationRepository _sut = null!;

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync();
        var options = Options.Create(new MongoDbOptions
        {
            ConnectionString = _runner.ConnectionString,
            DatabaseName = "test_db"
        });
        _sut = new ConversationRepository(new MongoDbContext(options));
    }

    public Task DisposeAsync()
    {
        _runner.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetUserContextAsync_ReturnsEmptyContext_WhenNotFound()
    {
        var context = await _sut.GetUserContextAsync(1UL);

        Assert.Equal(1UL, context.TargetId);
        Assert.Equal(ContextType.User, context.Type);
        Assert.Empty(context.Messages);
    }

    [Fact]
    public async Task GetChannelContextAsync_ReturnsEmptyContext_WhenNotFound()
    {
        var context = await _sut.GetChannelContextAsync(2UL);

        Assert.Equal(2UL, context.TargetId);
        Assert.Equal(ContextType.Channel, context.Type);
        Assert.Empty(context.Messages);
    }

    [Fact]
    public async Task SaveContextAsync_PersistsAndRetrievesContext()
    {
        var context = new ConversationContext
        {
            TargetId = 100UL,
            Type = ContextType.User,
            Messages = [new() { Role = Role.User, Content = "hello" }]
        };

        await _sut.SaveContextAsync(context);
        var retrieved = await _sut.GetUserContextAsync(100UL);

        Assert.Single(retrieved.Messages);
        Assert.Equal("hello", retrieved.Messages[0].Content);
    }

    [Fact]
    public async Task SaveContextAsync_UpdatesExistingContext()
    {
        var context = new ConversationContext { TargetId = 200UL, Type = ContextType.User };
        context.Messages.Add(new() { Role = Role.User, Content = "first" });
        await _sut.SaveContextAsync(context);

        context.Messages.Add(new() { Role = Role.Assistant, Content = "second" });
        await _sut.SaveContextAsync(context);

        var retrieved = await _sut.GetUserContextAsync(200UL);
        Assert.Equal(2, retrieved.Messages.Count);
    }

    [Fact]
    public async Task ClearContextAsync_RemovesAllMessages()
    {
        var context = new ConversationContext
        {
            TargetId = 300UL,
            Type = ContextType.Channel,
            Messages = [new() { Role = Role.User, Content = "msg" }]
        };
        await _sut.SaveContextAsync(context);

        await _sut.ClearContextAsync(context);
        var retrieved = await _sut.GetChannelContextAsync(300UL);

        Assert.Empty(retrieved.Messages);
    }
}
