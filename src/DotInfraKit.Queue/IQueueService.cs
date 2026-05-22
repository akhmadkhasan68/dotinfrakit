namespace DotInfraKit.Queue;

public interface IQueueService
{
    Task<Guid> EnqueueAsync<TJob, TPayload>(TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>;

    Task<Guid> EnqueueAsync<TJob, TPayload>(string queueName, TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>;
}
