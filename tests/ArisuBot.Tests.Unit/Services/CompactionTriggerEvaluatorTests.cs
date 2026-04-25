using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using ArisuBot.Core.Services;
using Microsoft.Extensions.Options;

namespace ArisuBot.Tests.Unit.Services;

public class CompactionTriggerEvaluatorTests
{
    private static CompactionTriggerEvaluator CreateSut(CompactionOptions options)
        => new(Options.Create(options));

    private static ConversationContext MakeContext(
        int lastTotalTokens = 0,
        DateTime? updatedAt = null,
        DateTime? lastCompactedAt = null) => new()
    {
        LastTotalTokens = lastTotalTokens,
        UpdatedAt       = updatedAt ?? DateTime.UtcNow,
        LastCompactedAt = lastCompactedAt
    };

    // --- Enabled / Cooldown ---

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenEnabledIsFalse()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = false, TokenThresholdEnabled = true, TokenThreshold = 1 });
        var context = MakeContext(lastTotalTokens: 100);
        Assert.False(sut.ShouldCompact(context));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenWithinCooldown()
    {
        var sut = CreateSut(new CompactionOptions
        {
            Enabled = true, CooldownMinutes = 60,
            TokenThresholdEnabled = true, TokenThreshold = 1
        });
        var recentCompact = DateTime.UtcNow.AddMinutes(-30);
        var context = MakeContext(lastTotalTokens: 100, lastCompactedAt: recentCompact);
        Assert.False(sut.ShouldCompact(context));
    }

    [Fact]
    public void ShouldCompact_ReturnsTrue_WhenCooldownExpired()
    {
        var sut = CreateSut(new CompactionOptions
        {
            Enabled = true, CooldownMinutes = 60,
            TokenThresholdEnabled = true, TokenThreshold = 1
        });
        var oldCompact = DateTime.UtcNow.AddMinutes(-90);
        var context = MakeContext(lastTotalTokens: 100, lastCompactedAt: oldCompact);
        Assert.True(sut.ShouldCompact(context));
    }

    [Fact]
    public void ShouldCompact_ReturnsTrue_WhenNeverCompacted()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, TokenThresholdEnabled = true, TokenThreshold = 1 });
        var context = MakeContext(lastTotalTokens: 100);
        Assert.True(sut.ShouldCompact(context));
    }

    // --- Token 트리거 ---

    [Fact]
    public void IsTokenThresholdMet_ReturnsFalse_WhenDisabled()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, TokenThresholdEnabled = false, TokenThreshold = 1 });
        var context = MakeContext(lastTotalTokens: 9999);
        Assert.False(sut.IsTokenThresholdMet(context));
    }

    [Fact]
    public void IsTokenThresholdMet_ReturnsFalse_WhenBelowThreshold()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, TokenThresholdEnabled = true, TokenThreshold = 100 });
        var context = MakeContext(lastTotalTokens: 99);
        Assert.False(sut.IsTokenThresholdMet(context));
    }

    [Fact]
    public void IsTokenThresholdMet_ReturnsTrue_WhenAtThreshold()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, TokenThresholdEnabled = true, TokenThreshold = 100 });
        var context = MakeContext(lastTotalTokens: 100);
        Assert.True(sut.IsTokenThresholdMet(context));
    }

    // --- Inactivity 트리거 ---

    [Fact]
    public void IsInactivityMet_ReturnsFalse_WhenDisabled()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, InactivityEnabled = false, InactivityMinutes = 1 });
        var context = MakeContext(updatedAt: DateTime.UtcNow.AddDays(-1));
        Assert.False(sut.IsInactivityMet(context));
    }

    [Fact]
    public void IsInactivityMet_ReturnsFalse_WhenRecentlyUpdated()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, InactivityEnabled = true, InactivityMinutes = 60 });
        var context = MakeContext(updatedAt: DateTime.UtcNow.AddMinutes(-30));
        Assert.False(sut.IsInactivityMet(context));
    }

    [Fact]
    public void IsInactivityMet_ReturnsTrue_WhenInactiveEnough()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, InactivityEnabled = true, InactivityMinutes = 60 });
        var context = MakeContext(updatedAt: DateTime.UtcNow.AddMinutes(-90));
        Assert.True(sut.IsInactivityMet(context));
    }

    // --- Scheduled 트리거 ---

    [Fact]
    public void IsScheduledMet_ReturnsFalse_WhenDisabled()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, ScheduledEnabled = false, ScheduledTimeUtc = "03:00" });
        var context = MakeContext();
        var inWindow = DateTime.UtcNow.Date.AddHours(3);
        Assert.False(sut.IsScheduledMet(context, inWindow));
    }

    [Fact]
    public void IsScheduledMet_ReturnsFalse_WhenOutsideWindow()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, ScheduledEnabled = true, ScheduledTimeUtc = "03:00" });
        var context = MakeContext();
        var outside = DateTime.UtcNow.Date.AddHours(4); // 04:00 UTC, not in 03:00 window
        Assert.False(sut.IsScheduledMet(context, outside));
    }

    [Fact]
    public void IsScheduledMet_ReturnsTrue_WhenInsideWindowAndNeverCompacted()
    {
        var sut     = CreateSut(new CompactionOptions { Enabled = true, ScheduledEnabled = true, ScheduledTimeUtc = "03:00" });
        var context = MakeContext();
        var inWindow = DateTime.UtcNow.Date.AddHours(3); // 정확히 03:00 UTC
        Assert.True(sut.IsScheduledMet(context, inWindow));
    }

    [Fact]
    public void IsScheduledMet_ReturnsFalse_WhenAlreadyCompactedToday()
    {
        var sut = CreateSut(new CompactionOptions { Enabled = true, ScheduledEnabled = true, ScheduledTimeUtc = "03:00" });
        var windowStart = DateTime.UtcNow.Date.AddHours(3);
        // 오늘 03:00 이후 Compaction 실행됨
        var context = MakeContext(lastCompactedAt: windowStart.AddSeconds(10));
        var inWindow = windowStart;
        Assert.False(sut.IsScheduledMet(context, inWindow));
    }

    // --- OR 로직 ---

    [Fact]
    public void ShouldCompact_ReturnsTrue_WhenOnlyInactivityMet()
    {
        var sut = CreateSut(new CompactionOptions
        {
            Enabled              = true,
            TokenThresholdEnabled = false,
            InactivityEnabled    = true,
            InactivityMinutes    = 60,
            ScheduledEnabled     = false
        });
        var context = MakeContext(updatedAt: DateTime.UtcNow.AddHours(-2));
        Assert.True(sut.ShouldCompact(context));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenNoTriggerMet()
    {
        var sut = CreateSut(new CompactionOptions
        {
            Enabled              = true,
            TokenThresholdEnabled = true, TokenThreshold = 999999,
            InactivityEnabled    = false,
            ScheduledEnabled     = false
        });
        var context = MakeContext(lastTotalTokens: 1);
        Assert.False(sut.ShouldCompact(context));
    }
}
