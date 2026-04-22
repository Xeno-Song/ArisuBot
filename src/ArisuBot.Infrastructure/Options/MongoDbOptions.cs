namespace ArisuBot.Infrastructure.Options;

/// <summary>MongoDB 연결 설정.</summary>
public class MongoDbOptions
{
    public const string SectionName = "MongoDB";
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
}
