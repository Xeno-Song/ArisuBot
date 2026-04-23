using ArisuBot.Core.Interfaces;
using ArisuBot.Discord.Options;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Discord.Services;

/// <summary>관리자 계정에 Discord DM으로 알림을 전송한다.</summary>
public class AdminNotifier : IAdminNotifier
{
    private readonly IDiscordClient _client;
    private readonly DiscordOptions _options;
    private readonly ILogger<AdminNotifier> _logger;

    public AdminNotifier(
        IDiscordClient client,
        IOptions<DiscordOptions> options,
        ILogger<AdminNotifier> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>AdminUserIds 전원에게 DM을 전송한다. 개별 전송 실패 시 로그만 기록 (무한루프 방지).</summary>
    public async Task NotifyAsync(string message, CancellationToken ct = default)
    {
        foreach (var adminId in _options.AdminUserIds)
        {
            try
            {
                var user = await _client.GetUserAsync(adminId);
                if (user is null)
                {
                    _logger.LogWarning("관리자 알림 실패 — userId={UserId} 를 찾을 수 없음", adminId);
                    continue;
                }

                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                // 관리자 DM 실패는 로그만 기록. 재알림 없음 (무한루프 방지).
                _logger.LogError(ex, "관리자 알림 DM 전송 실패 — userId={UserId}", adminId);
            }
        }
    }
}
