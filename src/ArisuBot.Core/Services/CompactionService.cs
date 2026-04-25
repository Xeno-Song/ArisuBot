using System.Text;
using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Core.Services;

/// <summary>
/// 대화 컨텍스트 Compaction 실행.
/// LLM으로 facts/summary 추출 → old session 저장 → new session 생성.
/// </summary>
public class CompactionService : ICompactionService
{
    // CompactionResult JSON Schema — responseSchema로 Gemini에 전달해 구조화 출력 강제
    private const string CompactionResponseSchema = """
        {
          "type": "object",
          "properties": {
            "facts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "content":  { "type": "string" },
                  "subject":  { "type": "string" },
                  "category": {
                    "type": "string",
                    "enum": ["preference", "event", "decision", "status", "relationship", "rule"]
                  }
                },
                "required": ["content", "subject", "category"]
              }
            },
            "summary": { "type": "string" }
          },
          "required": ["facts", "summary"]
        }
        """;

    private readonly ILLMProvider        _llmProvider;
    private readonly IConversationRepository _repository;
    private readonly IPromptLoader       _promptLoader;
    private readonly CompactionOptions   _options;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        ILLMProvider llmProvider,
        IConversationRepository repository,
        IPromptLoader promptLoader,
        IOptions<CompactionOptions> options,
        ILogger<CompactionService> logger)
    {
        _llmProvider  = llmProvider;
        _repository   = repository;
        _promptLoader = promptLoader;
        _options      = options.Value;
        _logger       = logger;
    }

    /// <summary>
    /// Compaction 5단계 실행:
    /// 1. 기존 메시지 + compaction prompt로 LLM 호출 (responseSchema 강제)
    /// 2. JSON 파싱 → CompactionResult
    /// 3. old session에 결과 저장 (CompactionSummary, CompactionFacts, LastCompactedAt)
    /// 4. new session 생성: System + Persona + synthetic Assistant + 최근 N 메시지
    /// 5. new session 반환
    /// </summary>
    public async Task<ConversationContext> RunAsync(ConversationContext context, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[COMPACTION] 시작 — contextId={Id} msgCount={Count} lastTotalTokens={Tokens}",
            context.Id, context.Messages.Count, context.LastTotalTokens);

        // Step 1: LLM 호출 — 기존 메시지 전체 + compaction trigger 메시지
        var compactionInput = context.Messages
            .Append(new ChatMessage { Role = Role.User, Content = _promptLoader.CompactionPrompt })
            .ToList();

        var sb = new StringBuilder();
        await foreach (var response in _llmProvider.GenerateAsync(
            compactionInput,
            responseSchema: CompactionResponseSchema,
            ct: ct))
        {
            sb.Append(response.Content);
        }

        var rawJson = sb.ToString();
        _logger.LogInformation("[COMPACTION] LLM 응답 수신 — contextId={Id} length={Len}", context.Id, rawJson.Length);

        // Step 2: JSON 파싱 → CompactionResult
        var result = ParseCompactionResult(rawJson, context.Id);

        // Step 3: old session에 결과 저장
        context.CompactionSummary  = result.Summary;
        context.CompactionFacts    = result.Facts;
        context.LastCompactedAt    = DateTime.UtcNow;
        await _repository.SaveContextAsync(context, ct);

        _logger.LogInformation(
            "[COMPACTION] old session 저장 완료 — contextId={Id} factCount={Count}",
            context.Id, result.Facts.Count);

        // Step 4: new session 생성
        var newContext = await CreateNewSessionAsync(context, result, ct);

        _logger.LogInformation(
            "[COMPACTION] 완료 — oldId={OldId} newId={NewId}",
            context.Id, newContext.Id);

        return newContext;
    }

    /// <summary>
    /// JSON 파싱. 실패 시 빈 CompactionResult 반환 — Compaction 자체는 계속 진행.
    /// </summary>
    private CompactionResult ParseCompactionResult(string rawJson, string contextId)
    {
        try
        {
            // LLM이 markdown 펜스로 감쌀 경우 제거
            var json = rawJson.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence    = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                    json = json[(firstNewline + 1)..lastFence].Trim();
            }

            var doc   = JsonDocument.Parse(json);
            var root  = doc.RootElement;
            var facts = new List<CompactionFact>();

            if (root.TryGetProperty("facts", out var factsEl))
            {
                foreach (var factEl in factsEl.EnumerateArray())
                {
                    facts.Add(new CompactionFact
                    {
                        Content  = factEl.TryGetProperty("content",  out var c) ? c.GetString() ?? "" : "",
                        Subject  = factEl.TryGetProperty("subject",  out var s) ? s.GetString() ?? "" : "",
                        Category = factEl.TryGetProperty("category", out var g) ? g.GetString() ?? "" : ""
                    });
                }
            }

            var summary = root.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString() ?? ""
                : "";

            return new CompactionResult { Facts = facts, Summary = summary };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[COMPACTION] JSON 파싱 실패 — contextId={Id} raw={Raw}",
                contextId, rawJson.Length > 200 ? rawJson[..200] + "..." : rawJson);
            return new CompactionResult();
        }
    }

    /// <summary>
    /// new session 초기화:
    /// [System] ← old session 원본 (캐시 prefix 보존)
    /// [User, Persona] ← old session 원본 (캐시 prefix 보존)
    /// [Assistant, synthetic] ← compaction context 주입
    /// [최근 N 메시지] ← old session에서 IsProtected 제외 후 TakeLast(N)
    /// </summary>
    private async Task<ConversationContext> CreateNewSessionAsync(
        ConversationContext old, CompactionResult result, CancellationToken ct)
    {
        // IsProtected: Role.System 또는 (Role.User && SenderName == null) → 항상 새 세션 선두에 복사
        var protectedMessages = old.Messages
            .Where(IsProtected)
            .ToList();

        var conversationMessages = old.Messages
            .Where(m => !IsProtected(m))
            .ToList();

        var recentMessages = conversationMessages
            .TakeLast(_options.InjectionRecentMessageCount)
            .ToList();

        // synthetic Assistant 메시지: Compaction 결과 요약 주입
        var syntheticContent = BuildSyntheticContent(result);
        var syntheticMessage = new ChatMessage
        {
            Role    = Role.Assistant,
            Content = syntheticContent
        };

        // new session 생성 (빈 도큐먼트 insert)
        var newContext = await _repository.CreateNewSessionAsync(old.TargetId, old.Type, ct);

        // Participants 복사
        newContext.Participants = new Dictionary<string, ulong>(old.Participants);

        // 메시지 순서대로 append: protected → synthetic → recent N
        foreach (var msg in protectedMessages)
            await _repository.SaveContextAsync(AppendMessage(newContext, msg), ct);

        await _repository.SaveContextAsync(AppendMessage(newContext, syntheticMessage), ct);

        foreach (var msg in recentMessages)
            await _repository.SaveContextAsync(AppendMessage(newContext, msg), ct);

        return newContext;
    }

    /// <summary>Compaction 결과를 Assistant 메시지 텍스트로 변환.</summary>
    private static string BuildSyntheticContent(CompactionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[이전 대화 요약]");
        sb.AppendLine(result.Summary);

        if (result.Facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[주요 사실]");
            foreach (var fact in result.Facts)
                sb.AppendLine($"- [{fact.Category}] {fact.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>IsProtected: 새 세션에서 압축 대상에서 제외하는 메시지 판별.</summary>
    private static bool IsProtected(ChatMessage m)
        => m.Role == Role.System || (m.Role == Role.User && m.SenderName is null);

    /// <summary>context.Messages에 추가 후 context 반환 (메서드 체인용).</summary>
    private static ConversationContext AppendMessage(ConversationContext context, ChatMessage message)
    {
        context.Messages.Add(message);
        context.UpdatedAt = DateTime.UtcNow;
        return context;
    }
}
