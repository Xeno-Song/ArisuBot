using ArisuBot.Core.Models;

namespace ArisuBot.Core.Interfaces;

/// <summary>시스템 에러를 영속 저장소에 기록하는 추상화.</summary>
public interface IErrorLogger
{
    Task LogAsync(ErrorLogEntry entry, CancellationToken ct = default);
}
