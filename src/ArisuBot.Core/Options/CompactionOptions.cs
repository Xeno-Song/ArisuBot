namespace ArisuBot.Core.Options;

/// <summary>대화 컨텍스트 압축(Compaction) 트리거 및 실행 설정.</summary>
public class CompactionOptions
{
    public const string SectionName = "Compaction";

    /// <summary>Compaction 기능 활성화 여부. false면 모든 트리거 무시.</summary>
    public bool Enabled { get; set; } = true;

    // --- 트리거 1: Token 기반 ---

    /// <summary>Token 기반 트리거 활성화 여부.</summary>
    public bool TokenThresholdEnabled { get; set; } = true;

    /// <summary>마지막 LLM 요청의 총 토큰 수(tokensIn + tokensOut)가 이 값 이상이면 트리거.</summary>
    public int TokenThreshold { get; set; } = 150000;

    // --- 트리거 2: 비활성 시간 기반 ---

    /// <summary>비활성 시간 기반 트리거 활성화 여부.</summary>
    public bool InactivityEnabled { get; set; } = false;

    /// <summary>마지막 대화 후 이 분 이상 비활성 상태이면 트리거.</summary>
    public int InactivityMinutes { get; set; } = 1440;

    // --- 트리거 3: 특정 UTC 시각 기반 ---

    /// <summary>특정 UTC 시각 기반 트리거 활성화 여부. BackgroundService가 하루 1회 실행.</summary>
    public bool ScheduledEnabled { get; set; } = false;

    /// <summary>Compaction 실행 UTC 시각 (HH:mm 형식). ScheduledEnabled = true 시 적용.</summary>
    public string ScheduledTimeUtc { get; set; } = "03:00";

    // --- 중복 실행 방지 ---

    /// <summary>마지막 Compaction 실행 후 재실행 금지 최소 시간(분). 모든 트리거에 공통 적용.</summary>
    public int CooldownMinutes { get; set; } = 60;

    // --- 새 세션 초기화 ---

    /// <summary>Compaction 후 새 세션에 주입할 최근 메시지 수.</summary>
    public int InjectionRecentMessageCount { get; set; } = 20;
}
