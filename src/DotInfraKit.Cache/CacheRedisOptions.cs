namespace DotInfraKit.Cache;

public sealed class CacheRedisOptions
{
    public string Endpoint { get; set; } = "localhost:6379";
    public string? Password { get; set; }
    public int Database { get; set; } = 0;
    public string KeyPrefix { get; set; } = string.Empty;
}
