namespace DotInfraKit.Scheduler;

public interface IScheduledJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
