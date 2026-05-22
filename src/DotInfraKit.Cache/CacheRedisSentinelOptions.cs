namespace DotInfraKit.Cache;

public sealed class CacheRedisSentinelOptions
{
    public string ServiceName { get; set; } = string.Empty;
    public string[] Endpoints { get; set; } = [];
    public string? Password { get; set; }
    public int Database { get; set; } = 0;
    public string KeyPrefix { get; set; } = string.Empty;
}
