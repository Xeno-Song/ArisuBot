using Google.GenAI.Types;

namespace ArisuBot.LLM.Gemini;

/// <summary>Gemini 스트리밍 API 추상화. 단위 테스트에서 mock 교체 가능.</summary>
public interface IGeminiStreamClient
{
    IAsyncEnumerable<GenerateContentResponse> StreamAsync(
        string model, IEnumerable<Content> contents, GenerateContentConfig config);

    /// <summary>전송 전 토큰 수를 집계한다. 진단 로그 및 컨텍스트 크기 검증에 사용.</summary>
    Task<CountTokensResponse> CountTokensAsync(
        string model, List<Content> contents, CountTokensConfig config, CancellationToken ct = default);
}
