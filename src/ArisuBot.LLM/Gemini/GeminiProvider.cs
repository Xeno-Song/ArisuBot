using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Options;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.LLM.Gemini;

/// <summary>Google Gemini API를 사용하는 ILLMProvider 구현체.</summary>
public class GeminiProvider : ILLMProvider
{
    private readonly IGeminiStreamClient _streamClient;
    private readonly ILlmMonitorServer _pipeServer;
    private readonly GeminiOptions _geminiOptions;
    private readonly LLMOptions _llmOptions;
    private readonly ILogger<GeminiProvider> _logger;

    public string ProviderName => "Gemini";

    public GeminiProvider(
        IGeminiStreamClient streamClient,
        ILlmMonitorServer pipeServer,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<LLMOptions> llmOptions,
        ILogger<GeminiProvider> logger)
    {
        _streamClient   = streamClient;
        _pipeServer     = pipeServer;
        _geminiOptions  = geminiOptions.Value;
        _llmOptions     = llmOptions.Value;
        _logger         = logger;
    }

    /// <summary>
    /// ChatMessage 목록을 Gemini API로 전달해 응답을 스트리밍 생성한다.
    /// cacheHint가 제공되면 명시적 캐시 모드로 동작 — systemInstruction/tools는 캐시에 포함되므로 config에서 제외하고
    /// contents는 CachedMessageCount 이후 메시지만 전송한다.
    /// tools + toolContext가 모두 제공되면 함수 호출 루프를 실행한다.
    /// 텍스트와 FunctionCall이 동시 존재하는 iteration은 텍스트를 먼저 yield해 즉시 전달하고 이후 툴을 실행한다.
    /// </summary>
    public async IAsyncEnumerable<LLMResponse> GenerateAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<ILLMTool>? tools = null,
        LLMToolExecutionContext? toolContext = null,
        CacheHint? cacheHint = null,
        string? contextId = null,
        string? responseSchema = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageList = messages.ToList();

        List<Content> contents;
        var config = new GenerateContentConfig
        {
            MaxOutputTokens = _llmOptions.MaxTokens,
            Temperature     = _llmOptions.Temperature,
        };

        // responseSchema가 제공되면 구조화 출력 강제 — ResponseJsonSchema는 JSON Schema 직접 수용
        if (responseSchema is not null)
        {
            config.ResponseMimeType  = "application/json";
            config.ResponseJsonSchema = JsonSerializer.Deserialize<JsonElement>(responseSchema);
        }

        if (cacheHint != null)
        {
            // 명시적 캐시 모드: systemInstruction + tools는 캐시에 포함됨 → config에서 제외
            // cache는 항상 model role로 끝나도록 trim → 새 user Contents와 role 충돌 없음
            // → BuildContents(전체).Skip(cachedContentCount) 로 안전하게 신규 Contents 추출
            var allContents      = GeminiContentBuilder.BuildContents(messageList);
            contents             = allContents.Skip(cacheHint.CachedMessageCount).ToList();
            config.CachedContent = cacheHint.CachedContentName;

            _logger.LogDebug("캐시 모드 — cacheRef={CacheRef} skipCount={Skip} remainingContents={Count}",
                cacheHint.CachedContentName, cacheHint.CachedMessageCount, contents.Count);
        }
        else
        {
            // 일반 모드: 전체 컨텍스트 전송
            contents = GeminiContentBuilder.BuildContents(messageList);
            config.SystemInstruction = GeminiContentBuilder.BuildSystemInstruction(messageList);
            // tools와 toolContext 모두 있을 때만 툴 선언 추가
            config.Tools = (tools is { Count: > 0 } && toolContext is not null)
                ? GeminiContentBuilder.BuildToolDeclarations(tools)
                : null;
        }

        _logger.LogDebug("GenerateAsync 시작 — model={Model} messageCount={MessageCount} toolCount={ToolCount} cacheMode={CacheMode}",
            _geminiOptions.Model, messageList.Count, tools?.Count ?? 0, cacheHint != null);

        // 전송 전 토큰 집계 — 진단 로그
        try
        {
            var tokenCount = await _streamClient.CountTokensAsync(
                _geminiOptions.Model, contents, new CountTokensConfig(), ct);
            _logger.LogInformation(
                "[TokenCount] totalTokens={TotalTokens} cachedTokens={CachedTokens} contentCount={ContentCount}",
                tokenCount.TotalTokens, tokenCount.CachedContentTokenCount, contents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "사전 토큰 집계 실패");
        }

        // 이번 GenerateAsync 호출에서 실행된 Tool 호출 이력 — DB 저장 및 세션 복원용
        var toolCallHistory = new List<ChatMessage>();

        // yield 없는 iteration(FunctionCall-only)의 토큰을 다음 yield로 누적
        int pendingTokensIn = 0, pendingTokensOut = 0, pendingTokensCached = 0;

        // ServerError 재시도 + fallback 전환을 위한 현재 모델 추적 — 전환 후 sticky 유지
        var currentModel = _geminiOptions.Model;

        // 함수 호출 루프 — MaxToolIterations 초과 시 예외 발생
        for (var iteration = 0; iteration < _llmOptions.MaxToolIterations; iteration++)
        {
            _logger.LogDebug("LLM 스트리밍 요청 — iteration={Iteration}", iteration);

            var sb = new StringBuilder();
            GenerateContentResponseUsageMetadata? usage = null;
            // 스트리밍 도착 순서 그대로 모든 Part 누적 — text/thought/FunctionCall 인터리브 순서 보존
            var allParts = new List<Part>();

            // 재시도 루프: ServerError 시 동일 모델 재시도 후 FallbackModel로 전환
            var retries = 0;
            while (true)
            {
                sb.Clear();
                usage = null;
                allParts.Clear();

                try
                {
                    // 스트리밍 마지막 청크에 UsageMetadata 집계값이 포함됨 — 마지막 non-null 값을 보존
                    await foreach (var chunk in _streamClient.StreamAsync(currentModel, contents, config)
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
                    break; // 스트리밍 성공 — 재시도 루프 탈출
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ClientError ex)
                {
                    // 4xx ClientError — 재시도 없이 즉시 실패. 상세 원인은 exception 메시지에서 확인
                    _logger.LogError(ex, "Gemini ClientError — model={Model} iteration={Iteration}", currentModel, iteration);
                    throw;
                }
                catch (ServerError ex)
                {
                    retries++;
                    if (retries <= _geminiOptions.StreamRetryCount)
                    {
                        _logger.LogWarning(ex,
                            "Stream 실패, 동일 모델 재시도 — model={Model} attempt={Attempt} delayMs={DelayMs}",
                            currentModel, retries, _geminiOptions.RetryDelayMs);
                        if (_geminiOptions.RetryDelayMs > 0)
                            await Task.Delay(_geminiOptions.RetryDelayMs, ct);
                        continue;
                    }

                    // 재시도 소진 — fallback 전환 시도
                    if (_geminiOptions.FallbackModel is null || currentModel == _geminiOptions.FallbackModel)
                        throw; // fallback 없거나 이미 fallback 중이면 최종 실패

                    _logger.LogWarning(ex,
                        "Fallback 모델 전환 — from={Primary} to={Fallback}",
                        currentModel, _geminiOptions.FallbackModel);
                    _pipeServer.Emit(new ModelStatusEvent(
                        CurrentModel:  _geminiOptions.FallbackModel,
                        PreviousModel: currentModel));
                    currentModel = _geminiOptions.FallbackModel;
                    config.CachedContent = null; // fallback은 no-cache로 진행
                    retries = 0;
                    // continue: fallback 모델로 즉시 재시도 (딜레이 없음)
                }
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
            var nonFunctionCallParts = allParts.Where(p => p.FunctionCall is null).ToList();
            var providerMetadataJson = nonFunctionCallParts.Count > 0
                ? JsonSerializer.Serialize(nonFunctionCallParts)
                : null;

            // 함수 호출 없음 → 최종 응답 yield 후 종료
            if (functionCalls.Count == 0)
            {
                _logger.LogInformation(
                    "LLM 응답 완료 — tokensIn={TokensIn} tokensOut={TokensOut} tokensCached={TokensCached} textLength={TextLength}",
                    yieldTokensIn, yieldTokensOut, yieldTokensCached, sb.Length);

                _pipeServer.Emit(new TokenUsageEvent(
                    ContextId:    contextId,
                    TokensIn:     yieldTokensIn,
                    TokensOut:    yieldTokensOut,
                    TokensCached: yieldTokensCached,
                    Model:        currentModel));

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
                _pipeServer.Emit(new TokenUsageEvent(
                    ContextId:    contextId,
                    TokensIn:     yieldTokensIn,
                    TokensOut:    yieldTokensOut,
                    TokensCached: yieldTokensCached,
                    Model:        currentModel));
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
            contents.Add(new Content { Role = "model", Parts = allParts });

            // 각 함수를 실행하고 결과를 contents에 추가
            for (var fcIndex = 0; fcIndex < functionCalls.Count; fcIndex++)
            {
                var fc     = functionCalls[fcIndex];
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
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 예측 불가능한 런타임 예외 — LLM iteration 유지를 위해 ToolResult.Fail로 변환
                        _logger.LogError(ex, "툴 실행 예외 — callId={CallId} tool={ToolName}", callId, fc.Name);
                        toolResult = ToolResult.Fail(ex.Message);
                    }
                }

                _logger.LogInformation("툴 실행 완료 — tool={ToolName} success={Success}", fc.Name, toolResult.Success);

                var toolResultJson    = JsonSerializer.Serialize(toolResult, toolResult.GetType());
                var toolResultElement = JsonSerializer.Deserialize<JsonElement>(toolResultJson);

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

    // --- 하위 호환 위임 (테스트에서 직접 호출하는 경우를 위해 유지) ---

    /// <summary>GeminiContentBuilder.BuildSystemInstruction으로 위임.</summary>
    internal static Content? BuildSystemInstruction(IEnumerable<ChatMessage> messages)
        => GeminiContentBuilder.BuildSystemInstruction(messages);

    /// <summary>GeminiContentBuilder.BuildToolDeclarations으로 위임.</summary>
    internal static List<Tool> BuildToolDeclarations(IReadOnlyList<ILLMTool> tools)
        => GeminiContentBuilder.BuildToolDeclarations(tools);

    /// <summary>GeminiContentBuilder.BuildContents으로 위임.</summary>
    internal static List<Content> BuildContents(IEnumerable<ChatMessage> messages)
        => GeminiContentBuilder.BuildContents(messages);

    /// <summary>GeminiContentBuilder.MergeConsecutiveSameRole으로 위임.</summary>
    internal static List<Content> MergeConsecutiveSameRole(List<Content> contents)
        => GeminiContentBuilder.MergeConsecutiveSameRole(contents);

    /// <summary>GeminiContentBuilder.MergeConsecutiveTextParts으로 위임.</summary>
    internal static List<Part> MergeConsecutiveTextParts(IList<Part> parts)
        => GeminiContentBuilder.MergeConsecutiveTextParts(parts);

    /// <summary>GeminiContentBuilder.IsPureTextPart으로 위임.</summary>
    internal static bool IsPureTextPart(Part p)
        => GeminiContentBuilder.IsPureTextPart(p);

    /// <summary>GeminiContentBuilder.DeserializeMetadataParts으로 위임.</summary>
    internal static List<Part> DeserializeMetadataParts(string? metadataJson)
        => GeminiContentBuilder.DeserializeMetadataParts(metadataJson);
}
