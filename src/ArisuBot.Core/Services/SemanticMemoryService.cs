using System.Text;
using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Core.Services;

/// <summary>
/// 유저별 semantic memory 추출, 저장, 압축, 조회 서비스.
/// Compaction과 완전히 분리된 독립 서비스.
/// </summary>
public class SemanticMemoryService : ISemanticMemoryService
{
    // 추출 LLM responseSchema — users 배열, user별 subject/traits/events/episodes
    private const string ExtractionResponseSchema = """
        {
          "type": "object",
          "properties": {
            "users": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "subject":  { "type": "string" },
                  "traits":   { "type": "array", "items": { "type": "string" } },
                  "events":   { "type": "array", "items": { "type": "string" } },
                  "episodes": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["subject", "traits", "events", "episodes"]
              }
            }
          },
          "required": ["users"]
        }
        """;

    // 압축 LLM responseSchema — 문자열 배열
    private const string CompressionResponseSchema = """
        {
          "type": "array",
          "items": { "type": "string" }
        }
        """;

    private readonly ILLMProvider                _llmProvider;
    private readonly ISemanticMemoryRepository   _repository;
    private readonly IConversationRepository     _conversationRepository;
    private readonly IPromptLoader               _promptLoader;
    private readonly SemanticMemoryOptions       _options;
    private readonly ILogger<SemanticMemoryService> _logger;

    public SemanticMemoryService(
        ILLMProvider llmProvider,
        ISemanticMemoryRepository repository,
        IConversationRepository conversationRepository,
        IPromptLoader promptLoader,
        IOptions<SemanticMemoryOptions> options,
        ILogger<SemanticMemoryService> logger)
    {
        _llmProvider            = llmProvider;
        _repository             = repository;
        _conversationRepository = conversationRepository;
        _promptLoader           = promptLoader;
        _options                = options.Value;
        _logger                 = logger;
    }

    /// <summary>
    /// compactedContext.Messages 전체를 LLM에 제공해 유저별 semantic memory를 추출 및 저장한다.
    /// SemanticMemoryRefs.Any() == true이면 중복 방지를 위해 즉시 반환.
    /// </summary>
    public async Task ExtractAndSaveAsync(ConversationContext compactedContext, CancellationToken ct = default)
    {
        // 중복 방지 — 이미 이 세션에서 추출한 경우 skip
        if (compactedContext.SemanticMemoryRefs.Any())
        {
            _logger.LogDebug(
                "[SemanticMemory] 이미 추출된 세션 — contextId={Id}", compactedContext.Id);
            return;
        }

        _logger.LogInformation(
            "[SemanticMemory] 추출 시작 — contextId={Id} messageCount={Count}",
            compactedContext.Id, compactedContext.Messages.Count);

        // 추출 프롬프트를 User 메시지로 append
        var extractionMessages = compactedContext.Messages
            .Append(new ChatMessage { Role = Role.User, Content = _promptLoader.SemanticMemoryExtractionPrompt })
            .ToList();

        // 유효한 캐시가 있으면 hint 전달 → Gemini explicit cache hit 활용
        var cacheHint = BuildCacheHint(compactedContext);

        var sb = new StringBuilder();
        await foreach (var response in _llmProvider.GenerateAsync(
            extractionMessages,
            tools: null,
            toolContext: null,
            cacheHint: cacheHint,
            contextId: compactedContext.Id,
            responseSchema: ExtractionResponseSchema,
            ct: ct))
        {
            sb.Append(response.Content);
        }

        var rawJson = sb.ToString();
        _logger.LogInformation(
            "[SemanticMemory] LLM 응답 수신 — contextId={Id} length={Len}",
            compactedContext.Id, rawJson.Length);

        var userEntries = ParseExtractionResult(rawJson, compactedContext.Id);
        if (userEntries.Count == 0)
        {
            _logger.LogInformation(
                "[SemanticMemory] 추출된 유저 없음 — contextId={Id}", compactedContext.Id);
            await _conversationRepository.SaveContextAsync(compactedContext, ct);
            return;
        }

        // 유저별 처리 — subject → userId 조회 후 저장
        foreach (var entry in userEntries)
        {
            if (!compactedContext.Participants.TryGetValue(entry.Subject, out var userId))
            {
                _logger.LogError(
                    "[SemanticMemory] subject '{Subject}' Participants에 없음 — contextId={Id}",
                    entry.Subject, compactedContext.Id);
                continue;
            }

            var facts = BuildFacts(entry, compactedContext.Id);
            if (facts.Count == 0)
            {
                compactedContext.SemanticMemoryRefs.Add(userId);
                continue;
            }

            await _repository.AppendFactsAsync(userId, facts, ct);

            // 저장 후 최신 상태 조회 → type별 threshold 확인
            await CompressIfNeededAsync(userId, ct);

            compactedContext.SemanticMemoryRefs.Add(userId);

            _logger.LogInformation(
                "[SemanticMemory] userId={UserId} facts={Count}개 저장 완료 — contextId={Id}",
                userId, facts.Count, compactedContext.Id);
        }

        await _conversationRepository.SaveContextAsync(compactedContext, ct);

        _logger.LogInformation(
            "[SemanticMemory] 추출 완료 — contextId={Id} processedUsers={Count}",
            compactedContext.Id, compactedContext.SemanticMemoryRefs.Count);
    }

    /// <summary>userId의 semantic memory fact 목록 반환. 없으면 빈 리스트.</summary>
    public async Task<IReadOnlyList<SemanticMemoryFact>> GetFactsAsync(ulong userId, CancellationToken ct = default)
    {
        var memory = await _repository.GetByUserIdAsync(userId, ct);
        return memory?.Facts ?? [];
    }

    // =========================================================
    // 내부 헬퍼
    // =========================================================

    /// <summary>유효한 cache가 있으면 CacheHint 생성, 없으면 null.</summary>
    private static CacheHint? BuildCacheHint(ConversationContext context)
    {
        if (context.DynamicCacheRef is null) return null;
        if (context.CacheExpiresAt is null || context.CacheExpiresAt <= DateTimeOffset.UtcNow) return null;
        return new CacheHint(context.DynamicCacheRef, context.CachedMessageCount);
    }

    /// <summary>LLM 응답 JSON 파싱 → (subject, traits, events, episodes) 목록 반환.</summary>
    private List<ExtractionEntry> ParseExtractionResult(string rawJson, string contextId)
    {
        try
        {
            var json = rawJson.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence    = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            var doc     = JsonDocument.Parse(json);
            var results = new List<ExtractionEntry>();

            if (!doc.RootElement.TryGetProperty("users", out var usersEl)) return results;

            foreach (var userEl in usersEl.EnumerateArray())
            {
                var subject  = userEl.TryGetProperty("subject",  out var s) ? s.GetString() ?? "" : "";
                var traits   = ReadStringArray(userEl, "traits");
                var events   = ReadStringArray(userEl, "events");
                var episodes = ReadStringArray(userEl, "episodes");

                if (!string.IsNullOrWhiteSpace(subject))
                    results.Add(new ExtractionEntry(subject, traits, events, episodes));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SemanticMemory] JSON 파싱 실패 — contextId={Id} raw={Raw}",
                contextId, rawJson.Length > 200 ? rawJson[..200] + "..." : rawJson);
            return [];
        }
    }

    private static List<string> ReadStringArray(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var arr)) return [];
        return arr.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    /// <summary>ExtractionEntry → SemanticMemoryFact 목록 변환.</summary>
    private static List<SemanticMemoryFact> BuildFacts(ExtractionEntry entry, string sourceContextId)
    {
        var now   = DateTime.UtcNow;
        var facts = new List<SemanticMemoryFact>();

        foreach (var t in entry.Traits)
            facts.Add(new SemanticMemoryFact { Content = t, Type = "trait",   SourceContextId = sourceContextId, ExtractedAt = now });
        foreach (var e in entry.Events)
            facts.Add(new SemanticMemoryFact { Content = e, Type = "event",   SourceContextId = sourceContextId, ExtractedAt = now });
        foreach (var ep in entry.Episodes)
            facts.Add(new SemanticMemoryFact { Content = ep, Type = "episode", SourceContextId = sourceContextId, ExtractedAt = now });

        return facts;
    }

    /// <summary>userId의 type별 fact 수가 threshold를 초과하면 압축 LLM 호출 후 교체한다.</summary>
    private async Task CompressIfNeededAsync(ulong userId, CancellationToken ct)
    {
        var memory = await _repository.GetByUserIdAsync(userId, ct);
        if (memory is null) return;

        var traitFacts   = memory.Facts.Where(f => f.Type == "trait").ToList();
        var eventFacts   = memory.Facts.Where(f => f.Type == "event").ToList();
        var episodeFacts = memory.Facts.Where(f => f.Type == "episode").ToList();
        var otherFacts   = memory.Facts.Where(f => f.Type is not ("trait" or "event" or "episode")).ToList();

        var compressed = new List<SemanticMemoryFact>(otherFacts);

        compressed.AddRange(await MaybeCompressAsync(traitFacts,   "trait",   _options.TraitCompressionThreshold,   userId, ct));
        compressed.AddRange(await MaybeCompressAsync(eventFacts,   "event",   _options.EventCompressionThreshold,   userId, ct));
        compressed.AddRange(await MaybeCompressAsync(episodeFacts, "episode", _options.EpisodeCompressionThreshold, userId, ct));

        // 압축이 발생한 경우에만 전체 교체
        var compressedAny =
            traitFacts.Count   >= _options.TraitCompressionThreshold   ||
            eventFacts.Count   >= _options.EventCompressionThreshold   ||
            episodeFacts.Count >= _options.EpisodeCompressionThreshold;

        if (compressedAny)
            await _repository.ReplaceFactsAsync(userId, compressed, ct);
    }

    /// <summary>threshold 이상이면 LLM으로 압축 후 반환. 미만이면 원본 그대로 반환.</summary>
    private async Task<List<SemanticMemoryFact>> MaybeCompressAsync(
        List<SemanticMemoryFact> facts,
        string type,
        int threshold,
        ulong userId,
        CancellationToken ct)
    {
        if (facts.Count < threshold) return facts;

        _logger.LogInformation(
            "[SemanticMemory] {Type} 압축 시작 — userId={UserId} count={Count} threshold={Threshold}",
            type, userId, facts.Count, threshold);

        // 압축 입력: 기존 content 목록 JSON + 압축 지시문
        var inputJson = JsonSerializer.Serialize(facts.Select(f => f.Content));
        var compressionMessages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = _promptLoader.SemanticMemoryCompressionPrompt + "\n\n" + inputJson }
        };

        var sb = new StringBuilder();
        await foreach (var response in _llmProvider.GenerateAsync(
            compressionMessages,
            tools: null,
            toolContext: null,
            cacheHint: null,
            contextId: null,
            responseSchema: CompressionResponseSchema,
            ct: ct))
        {
            sb.Append(response.Content);
        }

        var compressed = ParseCompressedFacts(sb.ToString(), type);

        _logger.LogInformation(
            "[SemanticMemory] {Type} 압축 완료 — userId={UserId} before={Before} after={After}",
            type, userId, facts.Count, compressed.Count);

        return compressed;
    }

    /// <summary>압축 LLM 응답 JSON(string[]) 파싱 → SemanticMemoryFact 목록.</summary>
    private List<SemanticMemoryFact> ParseCompressedFacts(string rawJson, string type)
    {
        try
        {
            var json = rawJson.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence    = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            var items = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            var now   = DateTime.UtcNow;
            return items
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => new SemanticMemoryFact
                {
                    Content         = s,
                    Type            = type,
                    SourceContextId = "compressed",
                    ExtractedAt     = now
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SemanticMemory] 압축 결과 파싱 실패 — type={Type} raw={Raw}",
                type, rawJson.Length > 200 ? rawJson[..200] + "..." : rawJson);
            return [];
        }
    }

    /// <summary>LLM 추출 결과 per-user 항목.</summary>
    private sealed record ExtractionEntry(
        string Subject,
        List<string> Traits,
        List<string> Events,
        List<string> Episodes);
}
