using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;

namespace ArisuBot.Tests.Unit.Infrastructure;

public class ConversationDocumentMappingTests
{
    [Fact]
    public void FromDomain_MapsAllFields()
    {
        var context = new ConversationContext
        {
            Id = "507f1f77bcf86cd799439011",
            Type = ContextType.User,
            TargetId = 123456789012345678UL,
            Messages =
            [
                new() { Role = Role.User, Content = "hello", Timestamp = DateTime.UtcNow }
            ],
            UpdatedAt = DateTime.UtcNow
        };

        var doc = ConversationDocument.FromDomain(context);

        Assert.Equal(context.Id, doc.Id);
        Assert.Equal("User", doc.Type);
        Assert.Equal("123456789012345678", doc.TargetId);
        Assert.Single(doc.Messages);
        Assert.Equal("User", doc.Messages[0].Role);
        Assert.Equal("hello", doc.Messages[0].Content);
    }

    [Fact]
    public void ToDomain_MapsAllFields()
    {
        var doc = new ConversationDocument
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "Channel",
            TargetId = "987654321098765432",
            Messages =
            [
                new() { Role = "Assistant", Content = "hi", Timestamp = DateTime.UtcNow }
            ],
            UpdatedAt = DateTime.UtcNow
        };

        var context = doc.ToDomain();

        Assert.Equal(doc.Id, context.Id);
        Assert.Equal(ContextType.Channel, context.Type);
        Assert.Equal(987654321098765432UL, context.TargetId);
        Assert.Single(context.Messages);
        Assert.Equal(Role.Assistant, context.Messages[0].Role);
    }

    [Fact]
    public void FromDomain_ConvertsUlongTargetIdToString()
    {
        var context = new ConversationContext { TargetId = ulong.MaxValue };
        var doc = ConversationDocument.FromDomain(context);
        Assert.Equal(ulong.MaxValue.ToString(), doc.TargetId);
    }

    [Fact]
    public void ToDomain_ParsesStringTargetIdToUlong()
    {
        var doc = new ConversationDocument { TargetId = ulong.MaxValue.ToString(), Type = "User" };
        var context = doc.ToDomain();
        Assert.Equal(ulong.MaxValue, context.TargetId);
    }
}
