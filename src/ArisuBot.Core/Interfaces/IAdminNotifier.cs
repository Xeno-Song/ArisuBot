namespace ArisuBot.Core.Interfaces;

/// <summary>관리자 Discord 계정에 알림 메시지를 전송한다.</summary>
public interface IAdminNotifier
{
    Task NotifyAsync(string message, CancellationToken ct = default);
}
