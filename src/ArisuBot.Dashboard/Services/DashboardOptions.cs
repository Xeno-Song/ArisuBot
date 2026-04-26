namespace ArisuBot.Dashboard.Services;

/// <summary>TCP 이벤트 서버 접속 옵션 (Host port 9876).</summary>
public class MonitorServerOptions
{
    public const string SectionName = "MonitorServer";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9876;
}

/// <summary>Host HTTP Control API 접속 옵션 (port 9877).</summary>
public class ControlApiOptions
{
    public const string SectionName = "ControlApi";
    public string BaseUrl { get; set; } = "http://localhost:9877";
}
