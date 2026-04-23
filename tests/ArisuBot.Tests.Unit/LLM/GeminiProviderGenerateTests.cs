using ArisuBot.Core.Models;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.LLM;

public class GeminiProviderGenerateTests
{
    private readonly Mock<IGeminiStreamClient> _streamMock = new();
    private readonly GeminiProvider _sut;

    public GeminiProviderGenerateTests()
    {
        var geminiOpts = Options.Create(new GeminiOptions { Model = "test-model", ApiKey = "key" });
        var llmOpts = Options.Create(new LLMOptions { MaxTokens = 100, Temperature = 0.5f });
        _sut = new GeminiProvider(_streamMock.Object, geminiOpts, llmOpts);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(
        params GenerateContentResponse[] items)
    {
        foreach (var item in items)
            yield return item;
    }

    private static GenerateContentResponse MakeChunk(string? text,
        int promptTokens = 0, int candidateTokens = 0, int cachedTokens = 0)
    {
        var response = new GenerateContentResponse();
        if (text is not null)
        {
            response.Candidates =
            [
                new Candidate
                {
                    Content = new Content { Parts = [new Part { Text = text }] }
                }
            ];
        }
        if (promptTokens > 0 || candidateTokens > 0 || cachedTokens > 0)
        {
            response.UsageMetadata = new GenerateContentResponseUsageMetadata
            {
                PromptTokenCount = promptTokens,
                CandidatesTokenCount = candidateTokens,
                CachedContentTokenCount = cachedTokens
            };
        }
        return response;
    }

    [Fact]
    public async Task GenerateAsync_AccumulatesTextChunks()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("hello "), MakeChunk("world")));

        var result = await _sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]);

        Assert.Single(result);
        Assert.Equal("hello world", result[0].Content);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsProviderName_Gemini()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("ok")));

        var result = await _sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]);

        Assert.Equal("Gemini", result[0].ProviderName);
    }

    [Fact]
    public async Task GenerateAsync_PopulatesTokenCounts_FromLastChunkUsageMetadata()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(
                MakeChunk("part1"),
                MakeChunk("part2", promptTokens: 100, candidateTokens: 50, cachedTokens: 20)));

        var result = await _sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]);

        Assert.Equal(100, result[0].TokensIn);
        Assert.Equal(50,  result[0].TokensOut);
        Assert.Equal(20,  result[0].TokensCachedIn);
    }

    [Fact]
    public async Task GenerateAsync_ZeroTokens_WhenNoUsageMetadata()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk("text")));

        var result = await _sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]);

        Assert.Equal(0, result[0].TokensIn);
        Assert.Equal(0, result[0].TokensOut);
        Assert.Equal(0, result[0].TokensCachedIn);
    }

    [Fact]
    public async Task GenerateAsync_EmptyContent_WhenNoTextChunks()
    {
        _streamMock.Setup(s => s.StreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<Content>>(), It.IsAny<GenerateContentConfig>()))
            .Returns(ToAsyncEnumerable(MakeChunk(null, promptTokens: 10, candidateTokens: 0)));

        var result = await _sut.GenerateAsync(
            [new ChatMessage { Role = Role.User, Content = "hi" }]);

        Assert.Equal(string.Empty, result[0].Content);
    }
}
