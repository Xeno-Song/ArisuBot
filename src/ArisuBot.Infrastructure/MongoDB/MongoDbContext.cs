using ArisuBot.Infrastructure.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace ArisuBot.Infrastructure.MongoDB;

/// <summary>MongoDB 클라이언트 및 데이터베이스 접근 진입점.</summary>
public class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IOptions<MongoDbOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
        _database = client.GetDatabase(options.Value.DatabaseName);
    }

    /// <summary>지정한 이름의 컬렉션을 반환한다.</summary>
    public IMongoCollection<T> GetCollection<T>(string name) =>
        _database.GetCollection<T>(name);
}
