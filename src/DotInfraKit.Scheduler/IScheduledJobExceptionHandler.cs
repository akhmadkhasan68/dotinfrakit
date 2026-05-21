namespace DotInfraKit.Scheduler;

public interface IScheduledJobExceptionHandler
{
    Task HandleAsync(Type jobType, Exception exception, CancellationToken cancellationToken);
}
