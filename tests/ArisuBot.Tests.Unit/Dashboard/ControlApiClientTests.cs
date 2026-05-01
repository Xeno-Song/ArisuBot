using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArisuBot.Dashboard.Services;

namespace ArisuBot.Tests.Unit.Dashboard;

/// <summary>ControlApiClient가 올바른 URL과 메서드로 HTTP 요청을 보내는지 검증.</summary>
public class ControlApiClientTests
{
    private static ControlApiClient CreateClient(MockHttpHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:9877") };
        return new ControlApiClient(http);
    }

    [Fact]
    public async Task GetToolsAsync_ReturnsToolDictionary()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK,
            """{"my_tool": true, "other_tool": false}""");
        var client = CreateClient(handler);

        var result = await client.GetToolsAsync();

        Assert.Equal("/api/tools", handler.LastRequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.True(result["my_tool"]);
        Assert.False(result["other_tool"]);
    }

    [Fact]
    public async Task SetToolEnabledAsync_SendsPutWithCorrectBody()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """{"name":"my_tool","enabled":false}""");
        var client = CreateClient(handler);

        await client.SetToolEnabledAsync("my_tool", false);

        Assert.Equal("/api/tools/my_tool", handler.LastRequestUri?.PathAndQuery);
        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        // PutAsJsonAsync 기본값: JsonSerializerDefaults.Web → camelCase
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task GetSessionsAsync_ReturnsSessionList()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK,
            """[{"id":"abc","type":"Channel","targetId":"123","messageCount":5,"createdAt":"2024-01-01T00:00:00Z","updatedAt":"2024-01-01T01:00:00Z"}]""");
        var client = CreateClient(handler);

        var result = await client.GetSessionsAsync();

        Assert.Equal("/api/sessions", handler.LastRequestUri?.PathAndQuery);
        Assert.Single(result);
        Assert.Equal("abc", result[0].Id);
    }

    [Fact]
    public async Task SetToolEnabledAsync_ThrowsOnError()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "error");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SetToolEnabledAsync("tool", true));
    }
}

/// <summary>HTTP 응답을 고정값으로 반환하는 테스트용 핸들러.</summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _responseBody;

    public Uri? LastRequestUri { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public string? LastRequestBody { get; private set; }

    public MockHttpHandler(HttpStatusCode status, string responseBody)
    {
        _status       = status;
        _responseBody = responseBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri  = request.RequestUri;
        LastMethod      = request.Method;
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : null;

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
    }
}
