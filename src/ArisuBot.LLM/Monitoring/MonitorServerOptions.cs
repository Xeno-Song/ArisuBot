namespace ArisuBot.LLM.Monitoring;

/// <summary>Monitor Sidecar TCP 서버 설정. Section: "MonitorServer".</summary>
public class MonitorServerOptions
{
    public const string SectionName = "MonitorServer";

    /// <summary>바인딩 주소. Docker 컨테이너 간 접근 시 "0.0.0.0" 사용.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>바인딩 포트.</summary>
    public int Port { get; set; } = 9876;
}
