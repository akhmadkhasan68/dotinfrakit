namespace DotInfraKit.Scheduler;

public sealed class ClusterOptions
{
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    internal string? ConnectionString { get; private set; }
    internal QuartzDbProvider Provider { get; private set; } = QuartzDbProvider.AutoDetect;

    public void UseDatabaseStore(string connectionString, QuartzDbProvider provider = QuartzDbProvider.AutoDetect)
    {
        ConnectionString = connectionString;
        Provider = provider;
    }
}
