namespace ArisuBot.Core.Interfaces;

/// <summary>메시지 처리 단위 이벤트 발행 추상화. MessageHandler에서 Monitor/Dashboard 에 처리 시작/완료를 알린다.</summary>
public interface IProcessingEventEmitter
{
    /// <summary>Discord 메시지 배치 처리 시작 시 호출.</summary>
    void EmitProcessingStarted(string contextId, int messageCount);

    /// <summary>Discord 메시지 배치 처리 완료 시 호출.</summary>
    void EmitProcessingCompleted(string contextId, long durationMs);
}
