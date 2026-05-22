namespace DotInfraKit.Queue.Redis;

public sealed class RedisClusterOptions
{
    public string[] Endpoints { get; set; } = [];
    public string? Password { get; set; }
    public string KeyPrefix { get; set; } = string.Empty;
}
