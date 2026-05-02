using System.Text;
using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Core.Services;

/// <summary>
/// 유저별 session semantic memory 추출, 저장, 압축, 조회 서비스.
/// session 종료 시 per-session document 생성: extracted(이번 session 추출분) + snapshot(누적).
/// Compaction과 완전히 분리된 독립 서비스.
/// </summary>
public class SemanticMemoryService : ISemanticMemoryService
{
    // 추출 LLM responseSchema — users 배열, user별 subject/traits/episodic
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
                  "episodic": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["subject", "traits", "episodic"]
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

    private readonly ILLMProvider                     _llmProvider;
    private readonly ISemanticMemoryRepository        _repository;
    private readonly IConversationRepository          _conversationRepository;
    private readonly IPromptLoader                    _promptLoader;
    private readonly SemanticMemoryOptions            _options;
    private readonly ILogger<SemanticMemoryService>   _logger;

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
    /// context.Messages 전체를 LLM에 제공해 유저별 semantic memory를 추출하고 저장한다.
    /// SemanticMemoryRefs.Any() == true이면 중복 방지를 위해 즉시 반환.
    /// 유저별로 이전 최신 active snapshot을 누적해 신규 per-session document를 생성한다.
    /// snapshot의 trait/episodic 개수가 threshold 초과 시 압축 시도 — 빈 결과면 원본 보존.
    /// </summary>
    public async Task ExtractAndSaveAsync(ConversationContext context, CancellationToken ct = default)
    {
        // 중복 방지 — 이미 이 세션에서 추출한 경우 skip
        if (context.SemanticMemoryRefs.Any())
        {
            _logger.LogDebug(
                "[SemanticMemory] 이미 추출된 세션 — contextId={Id}", context.Id);
            return;
        }

        _logger.LogInformation(
            "[SemanticMemory] 추출 시작 — contextId={Id} messageCount={Count}",
            context.Id, context.Messages.Count);

        // 추출 프롬프트를 User 메시지로 append
        var extractionMessages = context.Messages
            .Append(new ChatMessage { Role = Role.User, Content = _promptLoader.SemanticMemoryExtractionPrompt })
            .ToList();

        // 유효한 캐시가 있으면 hint 전달 → Gemini explicit cache hit 활용
        var cacheHint = BuildCacheHint(context);

        var sb = new StringBuilder();
        await foreach (var response in _llmProvider.GenerateAsync(
            extractionMessages,
            tools: null,
            toolContext: null,
            cacheHint: cacheHint,
            contextId: context.Id,
            responseSchema: ExtractionResponseSchema,
            ct: ct))
        {
            sb.Append(response.Content);
        }

        var rawJson = sb.ToString();
        _logger.LogInformation(
            "[SemanticMemory] LLM 응답 수신 — contextId={Id} length={Len}",
            context.Id, rawJson.Length);

        var userEntries = ParseExtractionResult(rawJson, context.Id);
        if (userEntries.Count == 0)
        {
            _logger.LogInformation(
                "[SemanticMemory] 추출된 유저 없음 — contextId={Id}", context.Id);
            await _conversationRepository.SaveContextAsync(context, ct);
            return;
        }

        // 유저별 처리 — subject → userId 조회 후 per-session document 생성
        foreach (var entry in userEntries)
        {
            if (!context.Participants.TryGetValue(entry.Subject, out var userId))
            {
                _logger.LogError(
                    "[SemanticMemory] subject '{Subject}' Participants에 없음 — contextId={Id}",
                    entry.Subject, context.Id);
                continue;
            }

            // 이번 session 추출분
            var extracted = new SemanticMemoryData
            {
                Traits   = new List<string>(entry.Traits),
                Episodic = new List<string>(entry.Episodic)
            };

            // 이전 최신 active snapshot 조회 → 누적 기반
            var previous = await _repository.GetLatestActiveAsync(userId, ct);
            var snapshot = BuildSnapshot(previous?.Snapshot, extracted);

            // threshold 초과 시 압축 — 빈 결과면 원본 보존
            snapshot = await CompressIfNeededAsync(snapshot, userId, ct);

            var memory = new UserSemanticMemory
            {
                UserId    = userId,
                SessionId = context.Id,
                State     = SemanticMemoryState.Active,
                Extracted = extracted,
                Snapshot  = snapshot,
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateAsync(memory, ct);
            context.SemanticMemoryRefs.Add(userId);

            _logger.LogInformation(
                "[SemanticMemory] userId={UserId} session document 생성 완료 — contextId={Id}",
                userId, context.Id);
        }

        await _conversationRepository.SaveContextAsync(context, ct);

        _logger.LogInformation(
            "[SemanticMemory] 추출 완료 — contextId={Id} processedUsers={Count}",
            context.Id, context.SemanticMemoryRefs.Count);
    }

    /// <summary>userId의 가장 최신 active snapshot 반환. 없으면 null.</summary>
    public async Task<SemanticMemoryData?> GetLatestSnapshotAsync(ulong userId, CancellationToken ct = default)
    {
        var memory = await _repository.GetLatestActiveAsync(userId, ct);
        return memory?.Snapshot;
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

    /// <summary>
    /// 이전 snapshot과 이번 extracted를 합쳐 신규 snapshot을 빌드한다.
    /// previous가 null이면 extracted 그대로 복사.
    /// </summary>
    private static SemanticMemoryData BuildSnapshot(SemanticMemoryData? previous, SemanticMemoryData extracted)
    {
        var traits   = new List<string>(previous?.Traits   ?? []);
        var episodic = new List<string>(previous?.Episodic ?? []);

        traits.AddRange(extracted.Traits);
        episodic.AddRange(extracted.Episodic);

        return new SemanticMemoryData { Traits = traits, Episodic = episodic };
    }

    /// <summary>
    /// snapshot의 traits/episodic 개수가 threshold 이상이면 LLM 압축 호출.
    /// 압축 결과가 비어 있거나 파싱 실패 시 원본 데이터 보존 (데이터 손실 방지).
    /// </summary>
    private async Task<SemanticMemoryData> CompressIfNeededAsync(
        SemanticMemoryData snapshot, ulong userId, CancellationToken ct)
    {
        var traits   = snapshot.Traits;
        var episodic = snapshot.Episodic;

        // traits 압축
        if (traits.Count >= _options.TraitCompressionThreshold)
        {
            _logger.LogInformation(
                "[SemanticMemory] traits 압축 시작 — userId={UserId} count={Count} threshold={Threshold}",
                userId, traits.Count, _options.TraitCompressionThreshold);

            var compressed = await CallCompressionAsync(traits, ct);
            // 빈 결과면 원본 보존
            traits = compressed.Count > 0 ? compressed : traits;

            _logger.LogInformation(
                "[SemanticMemory] traits 압축 완료 — userId={UserId} before={Before} after={After}",
                userId, snapshot.Traits.Count, traits.Count);
        }

        // episodic 압축
        if (episodic.Count >= _options.EpisodicCompressionThreshold)
        {
            _logger.LogInformation(
                "[SemanticMemory] episodic 압축 시작 — userId={UserId} count={Count} threshold={Threshold}",
                userId, episodic.Count, _options.EpisodicCompressionThreshold);

            var compressed = await CallCompressionAsync(episodic, ct);
            // 빈 결과면 원본 보존
            episodic = compressed.Count > 0 ? compressed : episodic;

            _logger.LogInformation(
                "[SemanticMemory] episodic 압축 완료 — userId={UserId} before={Before} after={After}",
                userId, snapshot.Episodic.Count, episodic.Count);
        }

        return new SemanticMemoryData { Traits = traits, Episodic = episodic };
    }

    /// <summary>항목 목록을 압축 LLM에 전달하고 압축된 목록을 반환. 파싱 실패 시 빈 리스트.</summary>
    private async Task<List<string>> CallCompressionAsync(List<string> items, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(items);
        var messages = new List<ChatMessage>
        {
            new() { Role = Role.User, Content = _promptLoader.SemanticMemoryCompressionPrompt + "\n\n" + inputJson }
        };

        var sb = new StringBuilder();
        await foreach (var response in _llmProvider.GenerateAsync(
            messages,
            tools: null,
            toolContext: null,
            cacheHint: null,
            contextId: null,
            responseSchema: CompressionResponseSchema,
            ct: ct))
        {
            sb.Append(response.Content);
        }

        return ParseCompressedList(sb.ToString());
    }

    /// <summary>LLM 응답 JSON 파싱 → (subject, traits, episodic) 목록 반환.</summary>
    private List<ExtractionEntry> ParseExtractionResult(string rawJson, string contextId)
    {
        try
        {
            var json = StripMarkdownFence(rawJson);
            var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("users", out var usersEl)) return [];

            var results = new List<ExtractionEntry>();
            foreach (var userEl in usersEl.EnumerateArray())
            {
                var subject  = userEl.TryGetProperty("subject",  out var s) ? s.GetString() ?? "" : "";
                var traits   = ReadStringArray(userEl, "traits");
                var episodic = ReadStringArray(userEl, "episodic");

                if (!string.IsNullOrWhiteSpace(subject))
                    results.Add(new ExtractionEntry(subject, traits, episodic));
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

    /// <summary>압축 LLM 응답 JSON(string[]) 파싱 → 문자열 목록. 파싱 실패 시 빈 리스트.</summary>
    private List<string> ParseCompressedList(string rawJson)
    {
        try
        {
            var json  = StripMarkdownFence(rawJson);
            var items = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return items.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SemanticMemory] 압축 결과 파싱 실패 — raw={Raw}",
                rawJson.Length > 200 ? rawJson[..200] + "..." : rawJson);
            return [];
        }
    }

    /// <summary>JSON 앞뒤의 ```...``` 마크다운 펜스를 제거한다.</summary>
    private static string StripMarkdownFence(string raw)
    {
        var json = raw.Trim();
        if (!json.StartsWith("```")) return json;

        var firstNewline = json.IndexOf('\n');
        var lastFence    = json.LastIndexOf("```");
        if (firstNewline >= 0 && lastFence > firstNewline)
            return json[(firstNewline + 1)..lastFence].Trim();

        return json;
    }

    private static List<string> ReadStringArray(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var arr)) return [];
        return arr.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    /// <summary>LLM 추출 결과 per-user 항목.</summary>
    private sealed record ExtractionEntry(
        string Subject,
        List<string> Traits,
        List<string> Episodic);
}
