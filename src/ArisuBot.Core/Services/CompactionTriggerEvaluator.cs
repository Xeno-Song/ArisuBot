using ArisuBot.Core.Models;
using ArisuBot.Core.Options;
using Microsoft.Extensions.Options;

namespace ArisuBot.Core.Services;

/// <summary>
/// Compaction 트리거 조건 평가. Token / Inactivity / Scheduled 세 조건을 OR 로직으로 평가.
/// 하나라도 충족하고 Cooldown을 지나면 실행 가능.
/// </summary>
public class CompactionTriggerEvaluator
{
    private readonly CompactionOptions _options;

    public CompactionTriggerEvaluator(IOptions<CompactionOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// 주어진 컨텍스트와 현재 시각을 기준으로 Compaction을 실행해야 하는지 판단한다.
    /// Enabled = false 이거나 Cooldown 내에 있으면 false 반환.
    /// </summary>
    public bool ShouldCompact(ConversationContext context, DateTime? utcNow = null)
    {
        if (!_options.Enabled) return false;

        var now = utcNow ?? DateTime.UtcNow;

        // Cooldown 체크: 마지막 Compaction 이후 CooldownMinutes 미경과 시 실행 금지
        if (context.LastCompactedAt.HasValue &&
            (now - context.LastCompactedAt.Value).TotalMinutes < _options.CooldownMinutes)
            return false;

        return IsTokenThresholdMet(context)
            || IsInactivityMet(context, now)
            || IsScheduledMet(context, now);
    }

    /// <summary>Token 기반 트리거: 마지막 LLM 총 토큰 >= TokenThreshold.</summary>
    public bool IsTokenThresholdMet(ConversationContext context)
        => _options.TokenThresholdEnabled
           && context.LastTotalTokens >= _options.TokenThreshold;

    /// <summary>비활성 트리거: 마지막 대화 후 InactivityMinutes 이상 경과.</summary>
    public bool IsInactivityMet(ConversationContext context, DateTime? utcNow = null)
    {
        if (!_options.InactivityEnabled) return false;
        var now = utcNow ?? DateTime.UtcNow;
        return (now - context.UpdatedAt).TotalMinutes >= _options.InactivityMinutes;
    }

    /// <summary>
    /// Scheduled 트리거: ScheduledTimeUtc(HH:mm)에 해당하는 1분 window 내이며
    /// 마지막 Compaction이 오늘 해당 시각 이전인 경우. BackgroundService가 1분 단위로 호출.
    /// </summary>
    public bool IsScheduledMet(ConversationContext context, DateTime? utcNow = null)
    {
        if (!_options.ScheduledEnabled) return false;

        var now = utcNow ?? DateTime.UtcNow;

        if (!TimeOnly.TryParse(_options.ScheduledTimeUtc, out var scheduled)) return false;

        var windowStart = now.Date.Add(scheduled.ToTimeSpan());
        var windowEnd   = windowStart.AddMinutes(1);

        // 현재 시각이 오늘 예약 window 내에 있어야 함
        if (now < windowStart || now >= windowEnd) return false;

        // 오늘 예약 시각 이후 Compaction이 이미 수행된 경우 재실행 금지
        if (context.LastCompactedAt.HasValue && context.LastCompactedAt.Value >= windowStart)
            return false;

        return true;
    }
}
