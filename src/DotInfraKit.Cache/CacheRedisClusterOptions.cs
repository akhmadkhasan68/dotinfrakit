namespace DotInfraKit.Cache;

public sealed class CacheRedisClusterOptions
{
    public string[] Endpoints { get; set; } = [];
    public string? Password { get; set; }
    public string KeyPrefix { get; set; } = string.Empty;
}
