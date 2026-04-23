using ArisuBot.Discord.Options;
using ArisuBot.Discord.Services;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ArisuBot.Tests.Unit.Discord;

public class AdminNotifierTests
{
    private readonly Mock<IDiscordClient> _clientMock = new();
    private readonly Mock<ILogger<AdminNotifier>> _loggerMock = new();

    private AdminNotifier CreateSut(params ulong[] adminIds)
    {
        var options = Options.Create(new DiscordOptions { AdminUserIds = adminIds });
        return new AdminNotifier(_clientMock.Object, options, _loggerMock.Object);
    }

    /// <summary>
    /// IDMChannel 및 IUser mock 구성 헬퍼.
    /// SendMessageAsync는 optional param 다수로 인해 직접 Setup 불가 (CS0854).
    /// Loose mock(기본)이 Task 반환 메서드에 자동으로 완료된 Task를 반환하므로 Setup 생략.
    /// DM 전송 여부는 GetUserAsync 호출 횟수로 간접 검증.
    /// </summary>
    private void SetupUserWithDm(ulong adminId)
    {
        var dmMock = new Mock<IDMChannel>(MockBehavior.Loose);
        var userMock = new Mock<IUser>(MockBehavior.Loose);

        // CreateDMChannelAsync: optional param 없는 오버로드 사용
        userMock.Setup(u => u.CreateDMChannelAsync(null))
            .ReturnsAsync(dmMock.Object);

        _clientMock.Setup(c => c.GetUserAsync(adminId, CacheMode.AllowDownload, null))
            .ReturnsAsync(userMock.Object);
    }

    [Fact]
    public async Task NotifyAsync_CallsGetUserAsync_ForEachAdminId()
    {
        SetupUserWithDm(111UL);
        var sut = CreateSut(111UL);

        await sut.NotifyAsync("test alert");

        _clientMock.Verify(
            c => c.GetUserAsync(111UL, CacheMode.AllowDownload, null),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_CallsGetUserAsync_ForAllAdminIds()
    {
        SetupUserWithDm(111UL);
        SetupUserWithDm(222UL);
        var sut = CreateSut(111UL, 222UL);

        await sut.NotifyAsync("broadcast");

        _clientMock.Verify(
            c => c.GetUserAsync(111UL, CacheMode.AllowDownload, null),
            Times.Once);
        _clientMock.Verify(
            c => c.GetUserAsync(222UL, CacheMode.AllowDownload, null),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_LogsWarning_WhenUserNotFound()
    {
        _clientMock.Setup(c => c.GetUserAsync(999UL, CacheMode.AllowDownload, null))
            .ReturnsAsync((IUser)null!);
        var sut = CreateSut(999UL);

        await sut.NotifyAsync("test");

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("찾을 수 없음")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_NoAdminIds_DoesNotCallGetUserAsync()
    {
        var sut = CreateSut(); // AdminUserIds = []

        await sut.NotifyAsync("no admins");

        _clientMock.Verify(
            c => c.GetUserAsync(It.IsAny<ulong>(), It.IsAny<CacheMode>(), It.IsAny<RequestOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyAsync_ContinuesToNextAdmin_WhenOneGetUserFails()
    {
        // 첫 번째 관리자는 GetUserAsync에서 예외 발생
        _clientMock.Setup(c => c.GetUserAsync(111UL, CacheMode.AllowDownload, null))
            .ThrowsAsync(new Exception("network error"));

        // 두 번째 관리자는 정상
        SetupUserWithDm(222UL);

        var sut = CreateSut(111UL, 222UL);

        // 예외 없이 완료 — 첫 번째 실패에도 두 번째 GetUserAsync 호출
        await sut.NotifyAsync("important");

        _clientMock.Verify(
            c => c.GetUserAsync(222UL, CacheMode.AllowDownload, null),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_LogsError_WhenSendThrows()
    {
        // CreateDMChannelAsync에서 예외 발생 → catch 블록에서 LogError 호출
        var userMock = new Mock<IUser>(MockBehavior.Loose);
        userMock.Setup(u => u.CreateDMChannelAsync(null))
            .ThrowsAsync(new Exception("dm error"));

        _clientMock.Setup(c => c.GetUserAsync(111UL, CacheMode.AllowDownload, null))
            .ReturnsAsync(userMock.Object);

        var sut = CreateSut(111UL);

        await sut.NotifyAsync("test");

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
