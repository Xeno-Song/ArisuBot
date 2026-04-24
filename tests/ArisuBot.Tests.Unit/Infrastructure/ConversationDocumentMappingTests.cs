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

    [Fact]
    public void FromDomain_MapsSenderName_WhenSet()
    {
        var context = new ConversationContext
        {
            TargetId = 1UL,
            Messages = [new() { Role = Role.User, Content = "hi", SenderName = "Xeno" }]
        };

        var doc = ConversationDocument.FromDomain(context);

        Assert.Equal("Xeno", doc.Messages[0].SenderName);
    }

    [Fact]
    public void ToDomain_MapsSenderName_WhenSet()
    {
        var doc = new ConversationDocument
        {
            Type = "User",
            TargetId = "1",
            Messages = [new ChatMessageDocument { Role = "User", Content = "hi", SenderName = "Xeno" }]
        };

        var context = doc.ToDomain();

        Assert.Equal("Xeno", context.Messages[0].SenderName);
    }

    [Fact]
    public void ToDomain_SenderNameIsNull_WhenNotInDocument()
    {
        var doc = new ConversationDocument
        {
            Type = "User",
            TargetId = "1",
            Messages = [new ChatMessageDocument { Role = "User", Content = "hi" }]
        };

        var context = doc.ToDomain();

        Assert.Null(context.Messages[0].SenderName);
    }

    [Fact]
    public void FromDomain_MapsParticipants()
    {
        var context = new ConversationContext
        {
            TargetId = 1UL,
            Participants = new Dictionary<string, ulong> { ["Xeno"] = 123456789UL }
        };

        var doc = ConversationDocument.FromDomain(context);

        Assert.Single(doc.Participants);
        Assert.Equal("123456789", doc.Participants["Xeno"]);
    }

    [Fact]
    public void ToDomain_MapsParticipants()
    {
        var doc = new ConversationDocument
        {
            Type = "User",
            TargetId = "1",
            Participants = new Dictionary<string, string> { ["Xeno"] = "123456789" }
        };

        var context = doc.ToDomain();

        Assert.Single(context.Participants);
        Assert.Equal(123456789UL, context.Participants["Xeno"]);
    }

    [Fact]
    public void ToDomain_EmptyParticipants_WhenNotInDocument()
    {
        var doc = new ConversationDocument { Type = "User", TargetId = "1" };

        var context = doc.ToDomain();

        Assert.Empty(context.Participants);
    }

    [Fact]
    public void FromDomain_MapsToolCallFields_WhenSet()
    {
        // Role.ToolCall 메시지의 CallId/ToolName/ToolArgsJson이 도큐먼트에 저장되는지 확인
        var callId = Guid.NewGuid();
        var context = new ConversationContext
        {
            TargetId = 1UL,
            Messages =
            [
                new()
                {
                    Role         = Role.ToolCall,
                    CallId       = callId,
                    ToolName     = "my_tool",
                    ToolArgsJson = """{"userName":"Xeno"}"""
                }
            ]
        };

        var doc = ConversationDocument.FromDomain(context);

        var msgDoc = doc.Messages[0];
        Assert.Equal("ToolCall", msgDoc.Role);
        Assert.Equal(callId.ToString(), msgDoc.CallId);
        Assert.Equal("my_tool", msgDoc.ToolName);
        Assert.Equal("""{"userName":"Xeno"}""", msgDoc.ToolArgsJson);
    }

    [Fact]
    public void ToDomain_MapsToolCallFields_WhenSet()
    {
        // 도큐먼트의 CallId/ToolName/ToolArgsJson이 도메인 모델로 복원되는지 확인
        var callId = Guid.NewGuid();
        var doc = new ConversationDocument
        {
            Type     = "User",
            TargetId = "1",
            Messages =
            [
                new ChatMessageDocument
                {
                    Role         = "ToolResponse",
                    Content      = """{"success":true}""",
                    CallId       = callId.ToString(),
                    ToolName     = "my_tool",
                    ToolArgsJson = null
                }
            ]
        };

        var context = doc.ToDomain();

        var msg = context.Messages[0];
        Assert.Equal(Role.ToolResponse, msg.Role);
        Assert.Equal(callId, msg.CallId);
        Assert.Equal("my_tool", msg.ToolName);
        Assert.Null(msg.ToolArgsJson);
    }

    [Fact]
    public void FromDomain_MapsProviderMetadataJson_WhenSet()
    {
        // Assistant/ToolCall 메시지의 ProviderMetadataJson(thought_signature 직렬화)이 도큐먼트에 저장되는지 확인
        var context = new ConversationContext
        {
            TargetId = 1UL,
            Messages =
            [
                new() { Role = Role.Assistant, Content = "hi", ProviderMetadataJson = """[{"thoughtSignature":"abc"}]""" }
            ]
        };

        var doc = ConversationDocument.FromDomain(context);

        Assert.Equal("""[{"thoughtSignature":"abc"}]""", doc.Messages[0].ProviderMetadataJson);
    }

    [Fact]
    public void ToDomain_MapsProviderMetadataJson_WhenSet()
    {
        // 도큐먼트의 ProviderMetadataJson이 도메인 모델로 복원되는지 확인
        var doc = new ConversationDocument
        {
            Type     = "User",
            TargetId = "1",
            Messages =
            [
                new ChatMessageDocument
                {
                    Role                 = "Assistant",
                    Content              = "hi",
                    ProviderMetadataJson = """[{"thoughtSignature":"abc"}]"""
                }
            ]
        };

        var context = doc.ToDomain();

        Assert.Equal("""[{"thoughtSignature":"abc"}]""", context.Messages[0].ProviderMetadataJson);
    }

    [Fact]
    public void ToDomain_ProviderMetadataJson_IsNullWhenNotInDocument()
    {
        // 레거시 문서(ProviderMetadataJson 필드 없음)도 정상 복원됨
        var doc = new ConversationDocument
        {
            Type     = "User",
            TargetId = "1",
            Messages = [new ChatMessageDocument { Role = "Assistant", Content = "hi" }]
        };

        var context = doc.ToDomain();

        Assert.Null(context.Messages[0].ProviderMetadataJson);
    }

    [Fact]
    public void ToDomain_ToolCallFields_AreNullWhenNotInDocument()
    {
        // Tool 전용 필드가 없는 기존 문서도 정상 복원됨 ([BsonIgnoreExtraElements] 동작 검증)
        var doc = new ConversationDocument
        {
            Type     = "User",
            TargetId = "1",
            Messages = [new ChatMessageDocument { Role = "User", Content = "hi" }]
        };

        var context = doc.ToDomain();

        Assert.Null(context.Messages[0].CallId);
        Assert.Null(context.Messages[0].ToolName);
        Assert.Null(context.Messages[0].ToolArgsJson);
    }
}
