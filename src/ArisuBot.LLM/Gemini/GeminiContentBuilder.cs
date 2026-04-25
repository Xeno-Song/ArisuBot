using System.Text.Json;
using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using Google.GenAI.Types;

namespace ArisuBot.LLM.Gemini;

/// <summary>
/// ChatMessage → Gemini Content/Tool 변환 유틸리티.
/// GeminiProvider와 GeminiCacheManager가 공유한다.
/// </summary>
internal static class GeminiContentBuilder
{
    /// <summary>Role.System 메시지를 Gemini SystemInstruction Content로 변환한다.</summary>
    internal static Content? BuildSystemInstruction(IEnumerable<ChatMessage> messages)
    {
        var systemText = string.Join("\n", messages
            .Where(m => m.Role == Role.System)
            .Select(m => m.Content));

        if (string.IsNullOrWhiteSpace(systemText)) return null;

        return new Content
        {
            Parts = [new Part { Text = systemText }]
        };
    }

    /// <summary>
    /// ILLMTool 목록을 Gemini Tool 선언 목록으로 변환한다.
    /// Tool 선언 순서를 Name 기준 정렬 — DI 등록 순서 변동에도 cache prefix 결정성 유지.
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

    /// <summary>
    /// Role.System 이외의 메시지를 Gemini Content 목록으로 변환한다.
    /// Role.ToolCall은 model의 FunctionCall Part로, Role.ToolResponse는 user의 FunctionResponse Part로 변환한다.
    /// 연속된 같은 Role Content를 병합해 cache prefix 안정성을 높인다.
    /// </summary>
    internal static List<Content> BuildContents(IEnumerable<ChatMessage> messages)
    {
        var result = new List<Content>();

        foreach (var m in messages.Where(msg => msg.Role != Role.System))
        {
            switch (m.Role)
            {
                case Role.ToolCall:
                    // LLM이 요청한 FunctionCall → model 역할 Content
                    var callArgs = m.ToolArgsJson is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, object>>(m.ToolArgsJson) ?? new()
                        : new Dictionary<string, object>();
                    // ProviderMetadataJson(thought_signature 등) 복원 → FunctionCall Part 앞에 위치
                    var toolCallParts = DeserializeMetadataParts(m.ProviderMetadataJson);
                    toolCallParts.Add(new Part { FunctionCall = new FunctionCall { Name = m.ToolName!, Args = callArgs } });
                    result.Add(new Content { Role = "model", Parts = toolCallParts });
                    break;

                case Role.ToolResponse:
                    // Tool 실행 결과 → user 역할 FunctionResponse Content
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
                    var role = m.Role == Role.User ? "user" : "model";
                    // Assistant: ProviderMetadataJson에 직렬화된 모든 non-FunctionCall Part 포함
                    // → 그대로 복원해야 cache prefix byte-identical 유지
                    if (m.Role == Role.Assistant && !string.IsNullOrEmpty(m.ProviderMetadataJson))
                    {
                        result.Add(new Content { Role = role, Parts = DeserializeMetadataParts(m.ProviderMetadataJson) });
                    }
                    else
                    {
                        // 레거시 Assistant(metadata 없음) 또는 User 메시지 → text 단일 Part로 fallback
                        // SenderName이 있으면 "[name]: content" 형태로 LLM에 전달해 발신자를 식별할 수 있게 함
                        var text = m.SenderName is not null ? $"[{m.SenderName}]: {m.Content}" : m.Content;
                        result.Add(new Content { Role = role, Parts = [new Part { Text = text }] });
                    }
                    break;
            }
        }

        // 연속 같은 Role Content 병합 → cache prefix 안정성
        return MergeConsecutiveSameRole(result);
    }

    /// <summary>연속된 같은 Role Content를 하나로 병합. Parts는 앞 Content 뒤에 append.</summary>
    internal static List<Content> MergeConsecutiveSameRole(List<Content> contents)
    {
        var merged = new List<Content>();
        foreach (var c in contents)
        {
            if (merged.Count > 0 && merged[^1].Role == c.Role)
            {
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
    /// 연속된 pure-text Part를 하나로 병합.
    /// ThoughtSignature/Thought/FunctionCall 등 메타 정보가 있는 Part는 그대로 보존.
    /// </summary>
    internal static List<Part> MergeConsecutiveTextParts(IList<Part> parts)
    {
        var merged = new List<Part>();
        foreach (var p in parts)
        {
            if (IsPureTextPart(p) && merged.Count > 0 && IsPureTextPart(merged[^1]))
                merged[^1] = new Part { Text = (merged[^1].Text ?? string.Empty) + (p.Text ?? string.Empty) };
            else
                merged.Add(p);
        }
        return merged;
    }

    /// <summary>Part가 오직 Text 필드만 세팅된 "순수 텍스트"인지 확인.</summary>
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
