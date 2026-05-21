namespace DotInfraKit.Scheduler;

public sealed class JobSchedulerBuilder
{
    private readonly List<(Type JobType, ScheduleBuilder Schedule)> _jobs = [];

    internal ClusterOptions? ClusterOptions { get; private set; }
    internal IReadOnlyList<(Type JobType, ScheduleBuilder Schedule)> Jobs => _jobs;

    public ScheduleBuilder Schedule<TJob>() where TJob : class, IScheduledJob
    {
        var builder = new ScheduleBuilder();
        _jobs.Add((typeof(TJob), builder));
        return builder;
    }

    public void UseClusterMode(Action<ClusterOptions> configure)
    {
        ClusterOptions = new ClusterOptions();
        configure(ClusterOptions);
    }
}
