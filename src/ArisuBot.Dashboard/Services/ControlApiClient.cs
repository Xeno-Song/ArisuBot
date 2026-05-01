using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace ArisuBot.Dashboard.Services;

/// <summary>Host HTTP Control API (port 9877) 클라이언트. tool 상태 조회/변경, 세션 조회.</summary>
public class ControlApiClient
{
    private readonly HttpClient _http;

    public ControlApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>모든 tool 활성화 상태 조회. key=toolName, value=enabled.</summary>
    public async Task<Dictionary<string, bool>> GetToolsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<Dictionary<string, bool>>("/api/tools", ct);
        return result ?? new Dictionary<string, bool>();
    }

    /// <summary>tool 활성화 상태 변경.</summary>
    public async Task SetToolEnabledAsync(string toolName, bool enabled, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/tools/{Uri.EscapeDataString(toolName)}",
            new { Enabled = enabled }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>활성 세션 목록 요약 조회.</summary>
    public async Task<List<SessionSummary>> GetSessionsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<SessionSummary>>("/api/sessions", ct);
        return result ?? [];
    }

    /// <summary>세션 토큰 합계 조회.</summary>
    public async Task<SessionTokenTotal?> GetSessionTokensAsync(string sessionId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<SessionTokenTotal>(
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}/tokens", ct);
    }
}

/// <summary>API 세션 요약 DTO.</summary>
public record SessionSummary(
    string Id,
    string Type,
    string TargetId,
    int MessageCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>API 세션 토큰 합계 DTO.</summary>
public record SessionTokenTotal(int TokensIn, int TokensOut, int TokensCachedIn, int RecordCount);
