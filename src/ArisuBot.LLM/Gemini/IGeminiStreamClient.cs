using Google.GenAI.Types;

namespace ArisuBot.LLM.Gemini;

/// <summary>Gemini 스트리밍 API 추상화. 단위 테스트에서 mock 교체 가능.</summary>
public interface IGeminiStreamClient
{
    IAsyncEnumerable<GenerateContentResponse> StreamAsync(
        string model, IEnumerable<Content> contents, GenerateContentConfig config);
}
