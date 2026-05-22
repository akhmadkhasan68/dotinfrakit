namespace DotInfraKit.IntegrationTests;

public sealed class RedisClusterSkipAttribute : FactAttribute
{
    internal const string EnvVar = "REDIS_CLUSTER_ENDPOINTS";

    public RedisClusterSkipAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Redis Cluster not configured. Set {EnvVar}=host:port,host:port,... to run cluster tests.";
    }

    internal static string[] GetEndpoints()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar) ?? string.Empty;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
