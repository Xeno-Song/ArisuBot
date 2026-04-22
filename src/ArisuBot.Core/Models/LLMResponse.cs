namespace ArisuBot.Core.Models;

/// <summary>LLM이 생성한 응답.</summary>
public record LLMResponse
{
    public string Content { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
}
