using System.Text;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;

namespace ArisuBot.LLM.Gemini;

/// <summary>Google Gemini API를 사용하는 ILLMProvider 구현체.</summary>
public class GeminiProvider : ILLMProvider
{
    private readonly IGeminiStreamClient _streamClient;
    private readonly GeminiOptions _geminiOptions;
    private readonly LLMOptions _llmOptions;

    public string ProviderName => "Gemini";

    public GeminiProvider(
        IGeminiStreamClient streamClient,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<LLMOptions> llmOptions)
    {
        _streamClient = streamClient;
        _geminiOptions = geminiOptions.Value;
        _llmOptions = llmOptions.Value;
    }

    /// <summary>ChatMessage 목록을 Gemini API로 전달해 스트리밍 응답을 누적한 뒤 반환한다.</summary>
    public async Task<IReadOnlyList<LLMResponse>> GenerateAsync(
        IEnumerable<ChatMessage> messages, CancellationToken ct = default)
    {
        var messageList = messages.ToList();

        var config = new GenerateContentConfig
        {
            MaxOutputTokens = _llmOptions.MaxTokens,
            Temperature = _llmOptions.Temperature,
            SystemInstruction = BuildSystemInstruction(messageList)
        };

        var contents = BuildContents(messageList);

        var sb = new StringBuilder();
        // 스트리밍 마지막 청크에 UsageMetadata 집계값이 포함됨 — 마지막 non-null 값을 보존
        GenerateContentResponseUsageMetadata? usage = null;
        await foreach (var chunk in _streamClient.StreamAsync(_geminiOptions.Model, contents, config)
            .WithCancellation(ct))
        {
            if (chunk.Text is not null)
                sb.Append(chunk.Text);
            if (chunk.UsageMetadata is not null)
                usage = chunk.UsageMetadata;
        }

        return
        [
            new LLMResponse
            {
                Content = sb.ToString(),
                ProviderName = ProviderName,
                TokensIn = usage?.PromptTokenCount ?? 0,
                TokensOut = usage?.CandidatesTokenCount ?? 0,
                TokensCachedIn = usage?.CachedContentTokenCount ?? 0
            }
        ];
    }

    /// <summary>Role.System 메시지를 Gemini SystemInstruction Content로 변환한다.</summary>
    internal static Content? BuildSystemInstruction(IEnumerable<ChatMessage> messages)
    {
        var systemText = string.Join("\n", messages
            .Where(m => m.Role == Core.Models.Role.System)
            .Select(m => m.Content));

        if (string.IsNullOrWhiteSpace(systemText)) return null;

        return new Content
        {
            Parts = [new Part { Text = systemText }]
        };
    }

    /// <summary>Role.System 이외의 메시지를 Gemini Content 목록으로 변환한다.</summary>
    internal static List<Content> BuildContents(IEnumerable<ChatMessage> messages)
        => messages
            .Where(m => m.Role != Core.Models.Role.System)
            .Select(m => new Content
            {
                Role = m.Role == Core.Models.Role.User ? "user" : "model",
                // SenderName이 있으면 "[name]: content" 형태로 LLM에 전달해 발신자를 식별할 수 있게 한다
                Parts = [new Part { Text = m.SenderName is not null ? $"[{m.SenderName}]: {m.Content}" : m.Content }]
            })
            .ToList();
}
