using System.Text.Json.Serialization;

namespace ArisuBot.Core.Models;

/// <summary>모든 LLM Tool 응답의 기반 레코드. JSON 직렬화 후 Gemini FunctionResponse.Response에 포함된다.</summary>
public record ToolResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>실패 결과 생성 헬퍼.</summary>
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>discord_timeout_user 툴 응답.</summary>
public record TimeoutUserResult : ToolResult
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>discord_list_channel_users 툴 응답.</summary>
public record ListChannelUsersResult : ToolResult
{
    [JsonPropertyName("memberCount")]
    public int MemberCount { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = [];
}
