using Google.GenAI;
using Google.GenAI.Types;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.LLM.Gemini;

/// <summary>실제 Google.GenAI Client를 사용하는 IGeminiStreamClient 구현체.</summary>
[ExcludeFromCodeCoverage(Justification = "Google.GenAI 실 API 클라이언트 래퍼 — 네트워크/API 키 필요. E2E 테스트 대상.")]
public class GeminiStreamClient : IGeminiStreamClient
{
    private readonly Client _client;

    public GeminiStreamClient(string apiKey)
    {
        _client = new Client(apiKey: apiKey);
    }

    /// <summary>Gemini 스트리밍 API 호출. 결과를 IAsyncEnumerable로 반환.</summary>
    public IAsyncEnumerable<GenerateContentResponse> StreamAsync(
        string model, IEnumerable<Content> contents, GenerateContentConfig config)
        => _client.Models.GenerateContentStreamAsync(model, contents.ToList(), config);

    /// <summary>스트리밍 전 토큰 수 집계. 디버그 로그용.</summary>
    public Task<CountTokensResponse> CountTokensAsync(
        string model, List<Content> contents, CountTokensConfig config, CancellationToken ct = default)
        => _client.Models.CountTokensAsync(model, contents, config, ct);
}
