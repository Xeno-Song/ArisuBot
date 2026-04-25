using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;

namespace ArisuBot.LLM.Gemini;

/// <summary>실제 Google.GenAI Client를 사용하는 IGeminiStreamClient + IGeminiCacheClient 구현체.</summary>
[ExcludeFromCodeCoverage(Justification = "Google.GenAI 실 API 클라이언트 래퍼 — 네트워크/API 키 필요. E2E 테스트 대상.")]
public class GeminiStreamClient : IGeminiStreamClient, IGeminiCacheClient
{
    private readonly Client _client;

    public GeminiStreamClient(string apiKey)
    {
        _client = new Client(apiKey: apiKey);
    }

    // --- IGeminiStreamClient ---

    /// <summary>Gemini 스트리밍 API 호출. 결과를 IAsyncEnumerable로 반환.</summary>
    public IAsyncEnumerable<GenerateContentResponse> StreamAsync(
        string model, IEnumerable<Content> contents, GenerateContentConfig config)
        => _client.Models.GenerateContentStreamAsync(model, contents.ToList(), config);

    /// <summary>전송 전 토큰 수 집계. 진단 로그용.</summary>
    public Task<CountTokensResponse> CountTokensAsync(
        string model, List<Content> contents, CountTokensConfig config, CancellationToken ct = default)
        => _client.Models.CountTokensAsync(model, contents, config, ct);

    // --- IGeminiCacheClient ---

    /// <summary>명시적 캐시를 생성한다.</summary>
    public Task<CachedContent> CreateAsync(
        string model,
        Content? systemInstruction,
        IEnumerable<Tool>? tools,
        IEnumerable<Content> contents,
        string ttl,
        CancellationToken ct = default)
    {
        var config = new CreateCachedContentConfig
        {
            Contents          = contents.ToList(),
            SystemInstruction = systemInstruction,
            Tools             = tools?.ToList(),
            Ttl               = ttl
        };
        return _client.Caches.CreateAsync(model: model, config: config, cancellationToken: ct);
    }

    /// <summary>명시적 캐시를 삭제한다.</summary>
    public Task DeleteAsync(string cacheName, CancellationToken ct = default)
        => _client.Caches.DeleteAsync(name: cacheName, cancellationToken: ct);

    /// <summary>등록된 명시적 캐시 목록을 스트리밍 반환한다.</summary>
    public async IAsyncEnumerable<CachedContent> ListAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pager = await _client.Caches.ListAsync(new ListCachedContentsConfig(), ct);
        await foreach (var cache in pager.WithCancellation(ct))
            yield return cache;
    }
}
