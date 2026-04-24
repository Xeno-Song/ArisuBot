using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Tools;

/// <summary>Discord 채널에 접근 권한이 있는 멤버 목록을 반환하는 LLM 툴.</summary>
public class ListChannelUsersTool : ILLMTool
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<ListChannelUsersTool> _logger;

    public string Name => "discord_list_channel_users";

    public LLMToolDefinition Definition => new()
    {
        Name = Name,
        // 전역 규칙 2.1: channelId를 파라미터로 받지 않음 — 항상 context.ChannelId(현재 채널) 사용
        Description = "Returns the list of server members who have access to the current channel.",
        ParametersJsonSchema = """
            {
              "type": "object",
              "properties": {}
            }
            """
    };

    public ListChannelUsersTool(DiscordSocketClient client, ILogger<ListChannelUsersTool> logger)
    {
        _client = client;
        _logger = logger;
    }

    [ExcludeFromCodeCoverage(Justification = "DiscordSocketClient.GetGuild, SocketGuildChannel, SocketGuildUser는 Discord.Net sealed 구체 타입 — E2E 테스트 대상.")]
    public Task<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object> arguments,
        LLMToolExecutionContext context,
        CancellationToken ct = default)
    {
        var guild = _client.GetGuild(context.GuildId);
        if (guild is null)
        {
            _logger.LogWarning("채널 멤버 조회 실패 — guildId={GuildId} 서버를 찾을 수 없음", context.GuildId);
            return Task.FromResult<ToolResult>(ToolResult.Fail("서버를 찾을 수 없습니다."));
        }

        // 전역 규칙 2.1: channelId는 항상 context에서 가져옴 — LLM이 제공하지 않음
        var channel = guild.GetChannel(context.ChannelId);
        if (channel is null)
        {
            _logger.LogWarning("채널 멤버 조회 실패 — guildId={GuildId} channelId={ChannelId} 채널을 찾을 수 없음",
                context.GuildId, context.ChannelId);
            return Task.FromResult<ToolResult>(ToolResult.Fail("채널을 찾을 수 없습니다."));
        }

        // 봇 제외 후 채널 ViewChannel 권한 보유 멤버 필터링
        // 전역 규칙 2.2: displayName만 반환 — userId(ID) 제외
        var members = guild.Users
            .Where(u => !u.IsBot && u.GetPermissions(channel).ViewChannel)
            .OrderBy(u => u.DisplayName)
            .Select(u => u.DisplayName)
            .ToList();

        _logger.LogInformation("채널 멤버 조회 완료 — guildId={GuildId} channelId={ChannelId} memberCount={Count}",
            context.GuildId, context.ChannelId, members.Count);

        return Task.FromResult<ToolResult>(new ListChannelUsersResult
        {
            Success = true,
            MemberCount = members.Count,
            Members = members
        });
    }
}
