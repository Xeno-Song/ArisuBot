namespace ArisuBot.Core.Options;

/// <summary>대화 컨텍스트 압축(Compaction) 트리거 설정.</summary>
public class CompactionOptions
{
    public const string SectionName = "Compaction";

    /// <summary>Compaction 기능 활성화 여부.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>마지막 LLM 요청의 총 토큰 수(tokensIn + tokensOut)가 이 값 이상이면 Compaction 트리거.</summary>
    public int TokenThreshold { get; set; } = 10000;
}
