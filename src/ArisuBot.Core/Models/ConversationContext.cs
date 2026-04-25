namespace ArisuBot.Core.Models;

/// <summary>유저 또는 채널 단위의 대화 컨텍스트.</summary>
public class ConversationContext
{
    public string Id { get; set; } = string.Empty;
    public ContextType Type { get; set; }
    public ulong TargetId { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    /// <summary>이 세션에서 발생한 LLM 요청별 토큰 사용량 이력.</summary>
    public List<TokenUsage> TokenUsage { get; set; } = new();
    /// <summary>대화에 참여한 유저 매핑. key: displayName, value: Discord userId. LLM <<name>> mention 치환에 사용.</summary>
    public Dictionary<string, ulong> Participants { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // --- 명시적 캐시 상태 ---

    /// <summary>Gemini 명시적 캐시 리소스 이름. 없으면 null.</summary>
    public string? DynamicCacheRef { get; set; }

    /// <summary>DynamicCacheRef에 포함된 비-System 메시지 수. GenerateAsync에서 contents를 skip할 기준.</summary>
    public int CachedMessageCount { get; set; }

    /// <summary>마지막 캐시 생성 이후 누적된 비캐시 입력 토큰 수. 캐시 갱신 임계값 비교에 사용.</summary>
    public int UncachedTokenCount { get; set; }

    /// <summary>최근 사용자 메시지 수신 시각 목록. velocity 조건 판단에 사용. GeminiCacheManager가 window 초과분을 prune한다.</summary>
    public List<DateTimeOffset> RecentMessageTimestamps { get; set; } = new();

    /// <summary>마지막 LLM 응답의 총 토큰 수 (tokensIn + tokensOut). 재시작 후 UncachedTokenCount 복원 기준값으로 사용.</summary>
    public int LastTotalTokens { get; set; }

    // --- Compaction 결과 저장 ---

    /// <summary>Compaction 실행으로 추출된 대화 요약. 새 세션 시작 시 Assistant 메시지로 주입.</summary>
    public string? CompactionSummary { get; set; }

    /// <summary>Compaction 실행으로 추출된 사실 목록.</summary>
    public List<CompactionFact>? CompactionFacts { get; set; }

    /// <summary>마지막 Compaction 실행 시각 (UTC). Cooldown 및 중복 실행 방지에 사용.</summary>
    public DateTime? LastCompactedAt { get; set; }
}

/// <summary>컨텍스트 범위 구분.</summary>
public enum ContextType
{
    User,
    Channel
}
