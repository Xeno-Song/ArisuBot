using ArisuBot.Core.Models;
using ArisuBot.Infrastructure.MongoDB.Documents;

namespace ArisuBot.Tests.Unit.Infrastructure;

public class ConversationDocumentMappingTests
{
    [Fact]
    public void FromDomain_MapsAllFields()
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var context = new ConversationContext
        {
            Id = "507f1f77bcf86cd799439011",
            Type = ContextType.User,
            TargetId = 123456789012345678UL,
            Messages =
            [
                new() { Role = Role.User, Content = "hello", Timestamp = DateTime.UtcNow }
            ],
            TokenUsage =
            [
                new() { TokensIn = 100, TokensOut = 50, TokensCachedIn = 10, Timestamp = createdAt }
            ],
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };

        var doc = ConversationDocument.FromDomain(context);

        Assert.Equal(context.Id, doc.Id);
        Assert.Equal("User", doc.Type);
        Assert.Equal("123456789012345678", doc.TargetId);
        Assert.Single(doc.Messages);
        Assert.Equal("User", doc.Messages[0].Role);
        Assert.Equal("hello", doc.Messages[0].Content);
        Assert.Single(doc.TokenUsage);
        Assert.Equal(100, doc.TokenUsage[0].TokensIn);
        Assert.Equal(50,  doc.TokenUsage[0].TokensOut);
        Assert.Equal(10,  doc.TokenUsage[0].TokensCachedIn);
        Assert.Equal(createdAt, doc.CreatedAt);
        Assert.Equal(updatedAt, doc.UpdatedAt);
    }

    [Fact]
    public void ToDomain_MapsAllFields()
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var doc = new ConversationDocument
        {
            Id = "507f1f77bcf86cd799439011",
            Type = "Channel",
            TargetId = "987654321098765432",
            Messages =
            [
                new() { Role = "Assistant", Content = "hi", Timestamp = DateTime.UtcNow }
            ],
            TokenUsage =
            [
                new() { TokensIn = 200, TokensOut = 80, TokensCachedIn = 20, Timestamp = createdAt }
            ],
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };

        var context = doc.ToDomain();

        Assert.Equal(doc.Id, context.Id);
        Assert.Equal(ContextType.Channel, context.Type);
        Assert.Equal(987654321098765432UL, context.TargetId);
        Assert.Single(context.Messages);
        Assert.Equal(Role.Assistant, context.Messages[0].Role);
        Assert.Single(context.TokenUsage);
        Assert.Equal(200, context.TokenUsage[0].TokensIn);
        Assert.Equal(80,  context.TokenUsage[0].TokensOut);
        Assert.Equal(20,  context.TokenUsage[0].TokensCachedIn);
        Assert.Equal(createdAt, context.CreatedAt);
        Assert.Equal(updatedAt, context.UpdatedAt);
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
