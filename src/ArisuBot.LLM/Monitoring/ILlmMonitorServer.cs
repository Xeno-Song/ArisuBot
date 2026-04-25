namespace ArisuBot.LLM.Monitoring;

/// <summary>LLM 상태 이벤트를 TCP로 Monitor Sidecar에 전송하는 인터페이스.</summary>
public interface ILlmMonitorServer
{
    /// <summary>
    /// 이벤트를 fire-and-forget으로 전송한다. Sidecar 미연결 시 drop.
    /// 모니터링 경로 실패는 봇 동작에 영향을 주지 않도록 설계됨 (선택 실행 지원).
    /// </summary>
    void Emit(LlmMonitorEvent evt);
}
