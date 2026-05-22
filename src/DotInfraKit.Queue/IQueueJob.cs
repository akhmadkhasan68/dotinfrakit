namespace DotInfraKit.Queue;

public interface IQueueJob<TPayload>
{
    Task ExecuteAsync(TPayload payload, JobContext context, CancellationToken cancellationToken);
}
