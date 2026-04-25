namespace ArisuBot.Core.Models;

/// <summary>
/// GenerateAsync에 명시적 캐시 정보를 전달하는 레코드.
/// 캐시를 지원하지 않는 provider는 null 수신 시 무시한다.
/// </summary>
/// <param name="CachedContentName">Gemini CachedContent 리소스 이름.</param>
/// <param name="CachedMessageCount">캐시에 포함된 비-System 메시지 수. BuildContents 결과에서 이 수만큼 skip.</param>
public record CacheHint(string CachedContentName, int CachedMessageCount);
