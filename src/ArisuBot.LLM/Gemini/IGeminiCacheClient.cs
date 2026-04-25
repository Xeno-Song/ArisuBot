using Google.GenAI.Types;

namespace ArisuBot.LLM.Gemini;

/// <summary>Gemini 명시적 캐시 CRUD 추상화. 단위 테스트 mock 교체 가능.</summary>
public interface IGeminiCacheClient
{
    /// <summary>명시적 캐시를 생성한다.</summary>
    Task<CachedContent> CreateAsync(
        string model,
        Content? systemInstruction,
        IEnumerable<Tool>? tools,
        IEnumerable<Content> contents,
        string ttl,
        CancellationToken ct = default);

    /// <summary>명시적 캐시를 삭제한다. 존재하지 않는 캐시 삭제 시 예외를 전파한다.</summary>
    Task DeleteAsync(string cacheName, CancellationToken ct = default);

    /// <summary>현재 등록된 명시적 캐시 목록을 스트리밍 반환한다.</summary>
    IAsyncEnumerable<CachedContent> ListAsync(CancellationToken ct = default);
}
