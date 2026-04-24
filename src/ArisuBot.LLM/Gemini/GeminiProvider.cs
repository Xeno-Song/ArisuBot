using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Options;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.LLM.Gemini;

/// <summary>Google Gemini API를 사용하는 ILLMProvider 구현체.</summary>
public class GeminiProvider : ILLMProvider
{
    private readonly IGeminiStreamClient _streamClient;
    private readonly GeminiOptions _geminiOptions;
    private readonly LLMOptions _llmOptions;
    private readonly ILogger<GeminiProvider> _logger;

    public string ProviderName => "Gemini";

    public GeminiProvider(
        IGeminiStreamClient streamClient,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<LLMOptions> llmOptions,
        ILogger<GeminiProvider> logger)
    {
        _streamClient = streamClient;
        _geminiOptions = geminiOptions.Value;
        _llmOptions = llmOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// ChatMessage 목록을 Gemini API로 전달해 응답을 스트리밍 생성한다.
    /// tools + toolContext가 모두 제공되면 함수 호출 루프를 실행한다.
    /// 텍스트와 FunctionCall이 동시 존재하는 iteration은 텍스트를 먼저 yield해 즉시 전달하고 이후 툴을 실행한다.
    /// </summary>
    public async IAsyncEnumerable<LLMResponse> GenerateAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ILLMTool>? tools = null,
        LLMToolExecutionContext? toolContext = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageList = messages.ToList();
        var contents = BuildContents(messageList);

        // tools와 toolContext 모두 있을 때만 툴 선언 추가
        var toolDeclarations = (tools is { Count: > 0 } && toolContext is not null)
            ? BuildToolDeclarations(tools)
            : null;

        var config = new GenerateContentConfig
        {
            MaxOutputTokens = _llmOptions.MaxTokens,
            Temperature = _llmOptions.Temperature,
            SystemInstruction = BuildSystemInstruction(messageList),
            Tools = toolDeclarations
        };

        _logger.LogDebug("GenerateAsync 시작 — model={Model} messageCount={MessageCount} toolCount={ToolCount}",
            _geminiOptions.Model, messageList.Count, tools?.Count ?? 0);

        // 디버그: 스트리밍 전 전체 컨텍스트 토큰 수 집계 — context 주입 확인용
        // CountTokensConfig.SystemInstruction은 Vertex AI 전용 — mldev(Gemini API)는 지원 안함
        try
        {
            var tokenCount = await _streamClient.CountTokensAsync(
                _geminiOptions.Model, contents, new CountTokensConfig(), ct);
            _logger.LogInformation(
                "[DEBUG] 사전 토큰 집계 — totalTokens={TotalTokens} cachedTokens={CachedTokens} contentCount={ContentCount}",
                tokenCount.TotalTokens, tokenCount.CachedContentTokenCount, contents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEBUG] 사전 토큰 집계 실패 — 무시하고 계속");
        }

        // 디버그: contents body + system/tool 전체를 파일로 덤프 — 연속 호출 간 diff로 prefix 차이 위치 식별
        try
        {
            var dumpPayload = new
            {
                model        = _geminiOptions.Model,
                systemInstr  = config.SystemInstruction,
                tools        = config.Tools,
                temperature  = config.Temperature,
                maxTokens    = config.MaxOutputTokens,
                contents
            };
            var dumpJson = JsonSerializer.Serialize(dumpPayload,
                new JsonSerializerOptions { WriteIndented = true });
            var dumpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arisubot_dump");
            System.IO.Directory.CreateDirectory(dumpDir);
            var dumpPath = System.IO.Path.Combine(dumpDir,
                $"contents_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
            await System.IO.File.WriteAllTextAsync(dumpPath, dumpJson, ct);
            _logger.LogInformation("[DEBUG] contents dump → {Path}", dumpPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DEBUG] contents dump 실패 — 무시하고 계속");
        }

        // 이번 GenerateAsync 호출에서 실행된 Tool 호출 이력 — DB 저장 및 세션 복원용
        var toolCallHistory = new List<ChatMessage>();

        // yield 없는 iteration(FunctionCall-only)의 토큰을 다음 yield로 누적
        int pendingTokensIn = 0, pendingTokensOut = 0, pendingTokensCached = 0;

        // 함수 호출 루프 — MaxToolIterations 초과 시 예외 발생
        for (var iteration = 0; iteration < _llmOptions.MaxToolIterations; iteration++)
        {
            _logger.LogDebug("LLM 스트리밍 요청 — iteration={Iteration}", iteration);

            var sb = new StringBuilder();
            GenerateContentResponseUsageMetadata? usage = null;
            // 스트리밍 도착 순서 그대로 모든 Part 누적 — text/thought/FunctionCall 인터리브 순서 보존
            // Gemini implicit cache는 model Content의 Parts 순서·내용 byte-identical 일치 요구
            var allParts = new List<Part>();

            // 스트리밍 마지막 청크에 UsageMetadata 집계값이 포함됨 — 마지막 non-null 값을 보존
            await foreach (var chunk in _streamClient.StreamAsync(_geminiOptions.Model, contents, config)
                .WithCancellation(ct))
            {
                if (chunk.Candidates is { Count: > 0 })
                {
                    foreach (var part in chunk.Candidates[0].Content?.Parts ?? [])
                    {
                        allParts.Add(part);
                        if (part.Text is not null)
                            sb.Append(part.Text);
                    }
                }
                if (chunk.UsageMetadata is not null)
                    usage = chunk.UsageMetadata;
            }

            // FunctionCall Part 추출 — 실행할 함수 목록
            var functionCalls = allParts
                .Where(p => p.FunctionCall is not null)
                .Select(p => p.FunctionCall!)
                .ToList();

            // 이번 iteration 토큰 + 이전 비-yield iteration 누적 토큰
            var yieldTokensIn     = (usage?.PromptTokenCount        ?? 0) + pendingTokensIn;
            var yieldTokensOut    = (usage?.CandidatesTokenCount    ?? 0) + pendingTokensOut;
            var yieldTokensCached = (usage?.CachedContentTokenCount ?? 0) + pendingTokensCached;

            // ProviderMetadataJson: FunctionCall 제외 모든 Part(text + thought 등)를 도착 순서 그대로 직렬화
            // BuildContents Assistant 케이스에서 그대로 Content.Parts 복원 → cache prefix byte-identical 유지
            // ToolCall 케이스에서는 FunctionCall이 별도 ChatMessage로 분리 저장되므로 metadata는 thought + text만 보유
            var nonFunctionCallParts = allParts.Where(p => p.FunctionCall is null).ToList();
            var providerMetadataJson = nonFunctionCallParts.Count > 0
                ? JsonSerializer.Serialize(nonFunctionCallParts)
                : null;

            // 함수 호출 없음 → 최종 응답 yield 후 종료
            if (functionCalls.Count == 0)
            {
                _logger.LogInformation(
                    "LLM 응답 완료 — tokensIn={TokensIn} tokensOut={TokensOut} textLength={TextLength}",
                    yieldTokensIn, yieldTokensOut, sb.Length);

                yield return new LLMResponse
                {
                    Content              = sb.ToString(),
                    ProviderName         = ProviderName,
                    TokensIn             = yieldTokensIn,
                    TokensOut            = yieldTokensOut,
                    TokensCachedIn       = yieldTokensCached,
                    ToolCallHistory      = toolCallHistory,
                    ProviderMetadataJson = providerMetadataJson
                };
                yield break;
            }

            _logger.LogInformation("툴 호출 감지 — tools=[{ToolNames}]",
                string.Join(", ", functionCalls.Select(fc => fc.Name)));

            // 텍스트와 FunctionCall이 동시 존재 → 텍스트 먼저 yield해 Discord에 즉시 전달
            if (sb.Length > 0)
            {
                _logger.LogInformation("중간 텍스트 응답 전송 — textLength={TextLength}", sb.Length);
                yield return new LLMResponse
                {
                    Content         = sb.ToString(),
                    ProviderName    = ProviderName,
                    TokensIn        = yieldTokensIn,
                    TokensOut       = yieldTokensOut,
                    TokensCachedIn  = yieldTokensCached,
                    ToolCallHistory = [] // 툴 실행 전 yield — ToolCallHistory는 최종 응답에 포함
                };
                // yield 후 pending 초기화 — 다음 yield는 이후 iteration 토큰만 포함
                pendingTokensIn = pendingTokensOut = pendingTokensCached = 0;
            }
            else
            {
                // FunctionCall만 있어 yield 없음 → 토큰을 다음 yield로 누적
                pendingTokensIn     = yieldTokensIn;
                pendingTokensOut    = yieldTokensOut;
                pendingTokensCached = yieldTokensCached;
            }

            // model Content 재구성 — 도착 순서 그대로 모든 Part 포함 (text + thought + FunctionCall)
            // 같은 GenerateAsync 내 다음 iteration의 Gemini 요청 contents에 추가됨
            contents.Add(new Content
            {
                Role  = "model",
                Parts = allParts    
            });

            // 각 함수를 실행하고 결과를 contents에 추가
            // 같은 iteration의 thought parts는 첫 ToolCall 메시지에 첨부 — DB 복원 시 model Content에 포함
            for (var fcIndex = 0; fcIndex < functionCalls.Count; fcIndex++)
            {
                var fc = functionCalls[fcIndex];
                // FunctionCall 단위로 내부 추적용 callId 생성 — LLM에 노출되지 않음
                var callId = Guid.NewGuid();

                var tool = tools?.FirstOrDefault(t => t.Name == fc.Name);
                if (tool is null)
                    _logger.LogWarning("알 수 없는 툴 이름 — toolName={ToolName}", fc.Name);

                var args = fc.Args ?? new Dictionary<string, object>();

                // ToolCall 이력 기록 — 첫 호출에만 thought metadata 첨부 (한 iteration 당 1회)
                toolCallHistory.Add(new ChatMessage
                {
                    Role                 = Role.ToolCall,
                    CallId               = callId,
                    ToolName             = fc.Name,
                    ToolArgsJson         = JsonSerializer.Serialize(args),
                    ProviderMetadataJson = fcIndex == 0 ? providerMetadataJson : null
                });

                ToolResult toolResult;
                if (tool is null)
                {
                    toolResult = ToolResult.Fail($"알 수 없는 툴 '{fc.Name}'");
                }
                else
                {
                    try
                    {
                        toolResult = await tool.ExecuteAsync(args, toolContext!, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // 취소 요청은 iteration 중단 — re-throw
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 예측 불가능한 런타임 예외 — LLM iteration 유지를 위해 ToolResult.Fail로 변환
                        _logger.LogError(ex, "툴 실행 예외 — callId={CallId} tool={ToolName}",
                            callId, fc.Name);
                        toolResult = ToolResult.Fail(ex.Message);
                    }
                }

                _logger.LogInformation("툴 실행 완료 — tool={ToolName} success={Success}",
                    fc.Name, toolResult.Success);

                // ToolResult JSON 직렬화 — runtime 타입 기준으로 파생 클래스 필드(memberCount 등) 포함
                // toolResult는 ToolResult로 선언되어 있어 Serialize<ToolResult>()는 기반 클래스 필드만 직렬화함
                var toolResultJson = JsonSerializer.Serialize(toolResult, toolResult.GetType());
                var toolResultElement = JsonSerializer.Deserialize<JsonElement>(toolResultJson);

                // ToolResponse 이력 기록 — Content에 JSON 저장해 세션 재시작 시 복원 가능
                toolCallHistory.Add(new ChatMessage
                {
                    Role     = Role.ToolResponse,
                    Content  = toolResultJson,
                    CallId   = callId,
                    ToolName = fc.Name
                });

                contents.Add(new Content
                {
                    Role  = "user",
                    Parts =
                    [
                        new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Name     = fc.Name,
                                Response = new Dictionary<string, object> { ["result"] = toolResultElement }
                            }
                        }
                    ]
                });
            }
        }

        throw new InvalidOperationException(
            $"툴 호출 루프가 최대 반복 횟수({_llmOptions.MaxToolIterations})를 초과했습니다.");
    }

    /// <summary>
    /// ILLMTool 목록을 Gemini Tool 선언 목록으로 변환한다.
    /// Tool 선언 순서를 Name 기준 정렬 — DI 등록 순서 변동에도 prefix 결정성 유지해 cache hit 안정화.
    /// </summary>
    internal static List<Tool> BuildToolDeclarations(IReadOnlyList<ILLMTool> tools) =>
    [
        new Tool
        {
            FunctionDeclarations = tools
                .OrderBy(t => t.Definition.Name, StringComparer.Ordinal)
                .Select(t => new FunctionDeclaration
                {
                    Name        = t.Definition.Name,
                    Description = t.Definition.Description,
                    // ParametersJsonSchema는 object? 타입 — string 전달 시 SDK가 JSON string literal로 직렬화해 API 거부.
                    // JsonElement로 파싱해야 raw JSON object로 직렬화됨.
                    ParametersJsonSchema = JsonSerializer.Deserialize<JsonElement>(
                        t.Definition.ParametersJsonSchema.Trim())
                }).ToList()
        }
    ];

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

    /// <summary>
    /// Role.System 이외의 메시지를 Gemini Content 목록으로 변환한다.
    /// Role.ToolCall은 model의 FunctionCall Part로, Role.ToolResponse는 user의 FunctionResponse Part로 변환한다.
    /// 이 메서드는 DB에서 복원된 이력(ToolCall/ToolResponse 포함)을 Gemini API 형식으로 재구성하는 데 사용된다.
    /// </summary>
    internal static List<Content> BuildContents(IEnumerable<ChatMessage> messages)
    {
        var result = new List<Content>();

        foreach (var m in messages.Where(msg => msg.Role != Core.Models.Role.System))
        {
            switch (m.Role)
            {
                case Core.Models.Role.ToolCall:
                    // LLM이 요청한 FunctionCall → model 역할 Content
                    var callArgs = m.ToolArgsJson is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, object>>(m.ToolArgsJson) ?? new()
                        : new Dictionary<string, object>();
                    // ProviderMetadataJson(thought_signature 등) 복원 → FunctionCall Part 앞에 위치
                    // Gemini API는 thought 후 FunctionCall 순서를 요구
                    var toolCallParts = DeserializeMetadataParts(m.ProviderMetadataJson);
                    toolCallParts.Add(new Part { FunctionCall = new FunctionCall { Name = m.ToolName!, Args = callArgs } });
                    result.Add(new Content
                    {
                        Role  = "model",
                        Parts = toolCallParts
                    });
                    break;

                case Core.Models.Role.ToolResponse:
                    // Tool 실행 결과 → user 역할 FunctionResponse Content
                    // Content는 ToolResult JSON — JsonElement로 파싱해 raw object로 전달
                    var responseElement = string.IsNullOrEmpty(m.Content)
                        ? JsonSerializer.Deserialize<JsonElement>("{}")
                        : JsonSerializer.Deserialize<JsonElement>(m.Content);
                    result.Add(new Content
                    {
                        Role  = "user",
                        Parts =
                        [
                            new Part
                            {
                                FunctionResponse = new FunctionResponse
                                {
                                    Name     = m.ToolName!,
                                    Response = new Dictionary<string, object> { ["result"] = responseElement }
                                }
                            }
                        ]
                    });
                    break;

                default:
                    // User / Assistant
                    var role = m.Role == Core.Models.Role.User ? "user" : "model";
                    // Assistant: ProviderMetadataJson에 도착 순서 그대로 직렬화된 모든 non-FunctionCall Part 포함
                    // → 그대로 복원해야 cache prefix byte-identical 유지. text는 metadata에 이미 포함되므로 m.Content 무시.
                    if (m.Role == Core.Models.Role.Assistant && !string.IsNullOrEmpty(m.ProviderMetadataJson))
                    {
                        result.Add(new Content { Role = role, Parts = DeserializeMetadataParts(m.ProviderMetadataJson) });
                    }
                    else
                    {
                        // 레거시 Assistant(metadata 없음) 또는 User 메시지 → text 단일 Part로 fallback
                        // SenderName이 있으면 "[name]: content" 형태로 LLM에 전달해 발신자를 식별할 수 있게 한다
                        var text = m.SenderName is not null ? $"[{m.SenderName}]: {m.Content}" : m.Content;
                        result.Add(new Content { Role = role, Parts = [new Part { Text = text }] });
                    }
                    break;
            }
        }

        // 테스트용 임시 변경: 연속 같은 Role Content 병합 → Parts concat
        // cache prefix 안정성 검증 목적. Gemini role(user/model) 기준 비교.
        return MergeConsecutiveSameRole(result);
    }

    /// <summary>연속된 같은 Role Content 를 하나로 병합. Parts 는 앞 Content 뒤에 append.</summary>
    internal static List<Content> MergeConsecutiveSameRole(List<Content> contents)
    {
        var merged = new List<Content>();
        foreach (var c in contents)
        {
            if (merged.Count > 0 && merged[^1].Role == c.Role)
            {
                // 이전 Content 와 Role 일치 → Parts 를 뒤쪽에 append
                var prev = merged[^1];
                var combined = new List<Part>(prev.Parts ?? new List<Part>());
                if (c.Parts is not null) combined.AddRange(c.Parts);
                merged[^1] = new Content { Role = prev.Role, Parts = MergeConsecutiveTextParts(combined) };
            }
            else
            {
                merged.Add(new Content { Role = c.Role, Parts = c.Parts is null ? c.Parts : MergeConsecutiveTextParts(c.Parts) });
            }
        }
        return merged;
    }

    /// <summary>
    /// 연속된 pure-text Part 를 하나로 병합. LLM 응답이 문자열 단위로 쪼개지는 경우 대응.
    /// ThoughtSignature/Thought/FunctionCall 등 메타 정보가 있는 Part 는 그대로 보존
    /// (cache prefix / reasoning state 유지).
    /// </summary>
    internal static List<Part> MergeConsecutiveTextParts(IList<Part> parts)
    {
        var merged = new List<Part>();
        foreach (var p in parts)
        {
            if (IsPureTextPart(p) && merged.Count > 0 && IsPureTextPart(merged[^1]))
            {
                // 이전 Part 도 순수 text → 문자열 연결
                merged[^1] = new Part { Text = (merged[^1].Text ?? string.Empty) + (p.Text ?? string.Empty) };
            }
            else
            {
                merged.Add(p);
            }
        }
        return merged;
    }

    /// <summary>Part 가 오직 Text 필드만 세팅된 "순수 텍스트" 인지 확인. 다른 모든 필드는 null/default 여야 함.</summary>
    internal static bool IsPureTextPart(Part p)
    {
        if (p.Text is null) return false;
        if (p.FunctionCall is not null) return false;
        if (p.FunctionResponse is not null) return false;
        if (p.InlineData is not null) return false;
        if (p.FileData is not null) return false;
        if (p.ExecutableCode is not null) return false;
        if (p.CodeExecutionResult is not null) return false;
        if (p.VideoMetadata is not null) return false;
        if (p.MediaResolution is not null) return false;
        if (p.ToolCall is not null) return false;
        if (p.ToolResponse is not null) return false;
        if (p.Thought is not null) return false;
        if (p.ThoughtSignature is not null) return false;
        if (p.PartMetadata is not null && p.PartMetadata.Count > 0) return false;
        return true;
    }

    /// <summary>ProviderMetadataJson(직렬화된 List&lt;Part&gt;) 역직렬화. null/빈 값이면 빈 리스트.</summary>
    internal static List<Part> DeserializeMetadataParts(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return new List<Part>();
        return JsonSerializer.Deserialize<List<Part>>(metadataJson) ?? new List<Part>();
    }
}
