using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Discord.Options;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Tools;

/// <summary>Discord 유저에게 타임아웃을 적용하는 LLM 툴.</summary>
public class TimeoutUserTool : ILLMTool
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordToolOptions _options;
    private readonly ILogger<TimeoutUserTool> _logger;

    public string Name => "discord_timeout_user";

    public LLMToolDefinition Definition => new()
    {
        Name = Name,
        Description = "Applies a timeout to a Discord user. During timeout, the user cannot chat, add reactions, or join voice channels. You MUST use ONLY username for userName property.",
        // 전역 규칙 2.2: LLM은 userId 대신 displayName(userName)으로 유저를 식별한다.
        ParametersJsonSchema = """
            {
              "type": "object",
              "properties": {
                "userName": {
                  "type": "string",
                  "description": "Display name of the Discord user to timeout. You MUST write username only."
                },
                "durationSeconds": {
                  "type": "integer",
                  "description": "Timeout duration in seconds"
                },
                "reason": {
                  "type": "string",
                  "description": "Reason for the timeout (optional)"
                }
              },
              "required": ["userName", "durationSeconds"]
            }
            """
    };

    public TimeoutUserTool(DiscordSocketClient client, IOptions<DiscordToolOptions> options,
        ILogger<TimeoutUserTool> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>파라미터 유효성 검사. 오류 시 오류 메시지 반환, 성공 시 null 반환.</summary>
    public static string? ValidateTimeoutParameters(
        IReadOnlyDictionary<string, object> arguments,
        DiscordToolOptions options,
        out string userName,
        out int durationSeconds)
    {
        userName = string.Empty;
        durationSeconds = 0;

        // userName: 필수, 공백이 아닌 문자열이어야 함
        if (!arguments.TryGetValue("userName", out var userNameObj)
            || userNameObj?.ToString() is not string userNameStr
            || string.IsNullOrWhiteSpace(userNameStr))
            return "오류: userName 파라미터가 필요합니다.";
        userName = userNameStr;

        if (!arguments.TryGetValue("durationSeconds", out var durationObj) || !TryParseInteger(durationObj, out durationSeconds))
            return "오류: durationSeconds 파라미터가 필요합니다.";

        // 범위 초과 시 clamp — error 반환 대신 허용 범위 내로 조정
        if (durationSeconds < options.TimeoutMinSeconds)
            durationSeconds = options.TimeoutMinSeconds;
        if (durationSeconds > options.TimeoutMaxSeconds)
            durationSeconds = options.TimeoutMaxSeconds;

        return null;
    }

    [ExcludeFromCodeCoverage(Justification = "DiscordSocketClient.GetGuild, SocketGuildUser.SetTimeOutAsync는 Discord.Net sealed 구체 타입 — E2E 테스트 대상.")]
    public async Task<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object> arguments,
        LLMToolExecutionContext context,
        CancellationToken ct = default)
    {
        var error = ValidateTimeoutParameters(arguments, _options, out var userName, out var durationSeconds);
        if (error is not null)
        {
            _logger.LogWarning("타임아웃 파라미터 검증 실패 — guildId={GuildId} error={Error}",
                context.GuildId, error);
            return ToolResult.Fail(error);
        }

        var reason = arguments.TryGetValue("reason", out var reasonObj) ? reasonObj?.ToString() : null;

        var guild = _client.GetGuild(context.GuildId);
        if (guild is null)
        {
            _logger.LogWarning("타임아웃 실패 — guildId={GuildId} 서버를 찾을 수 없음", context.GuildId);
            return ToolResult.Fail("서버를 찾을 수 없습니다.");
        }

        // 전역 규칙 2.2: displayName 기준으로 유저 resolve — LLM은 userId를 직접 다루지 않음
        var guildUser = guild.Users.FirstOrDefault(u => u.DisplayName == userName);
        if (guildUser is null)
        {
            _logger.LogWarning("타임아웃 실패 — guildId={GuildId} userName={UserName} 유저를 찾을 수 없음",
                context.GuildId, userName);
            return ToolResult.Fail($"유저 '{userName}'을(를) 서버에서 찾을 수 없습니다.");
        }

        try
        {
            await guildUser.SetTimeOutAsync(TimeSpan.FromSeconds(durationSeconds), new global::Discord.RequestOptions { CancelToken = ct });
        }
        catch (global::Discord.Net.HttpException ex) when ((int?)ex.DiscordCode == 50013)
        {
            // 50013 Missing Permissions — 봇 권한 부족 또는 역할 계층 문제
            _logger.LogWarning("Timeout failed — guildId={GuildId} userName={UserName} reason=MissingPermissions",
                context.GuildId, userName);
            return ToolResult.Fail("Missing permissions: bot lacks Moderate Members permission or target user's role is higher than the bot's role.");
        }

        _logger.LogInformation("Timeout applied — guildId={GuildId} userName={UserName} durationSeconds={Duration}",
            context.GuildId, userName, durationSeconds);

        return new TimeoutUserResult
        {
            Success = true,
            DisplayName = guildUser.DisplayName,
            DurationSeconds = durationSeconds,
            Reason = reason
        };
    }

    /// <summary>int, long, double, string 등 다양한 타입을 int로 파싱한다. Gemini SDK가 숫자를 다양한 타입으로 전달할 수 있음.</summary>
    internal static bool TryParseInteger(object? obj, out int value)
    {
        value = 0;
        return obj switch
        {
            null => false,
            int i => (value = i) >= 0,
            long l => (value = (int)l) >= 0,
            double d => (value = (int)d) >= 0,
            _ => int.TryParse(obj.ToString(), out value)
        };
    }
}
