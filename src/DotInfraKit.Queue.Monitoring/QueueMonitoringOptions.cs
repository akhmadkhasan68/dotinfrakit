namespace DotInfraKit.Queue.Monitoring;

public sealed class BasicAuthOptions
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class QueueMonitoringOptions
{
    public string Path { get; set; } = "/queue-monitoring";
    public BasicAuthOptions? BasicAuth { get; set; }
    public bool RequireAuthorization { get; set; }
    public string? AuthorizationPolicy { get; set; }
}
