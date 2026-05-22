using DotInfraKit.Queue;

namespace DotInfraKit.Testing;

public sealed class FakeQueueService : IQueueService
{
    private readonly record struct EnqueuedJob(Type JobType, object? Payload, string QueueName, EnqueueOptions? Options);
    private readonly List<EnqueuedJob> _jobs = [];

    public Task<Guid> EnqueueAsync<TJob, TPayload>(TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>
    {
        _jobs.Add(new EnqueuedJob(typeof(TJob), payload, "default", options));
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<Guid> EnqueueAsync<TJob, TPayload>(string queueName, TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>
    {
        _jobs.Add(new EnqueuedJob(typeof(TJob), payload, queueName, options));
        return Task.FromResult(Guid.NewGuid());
    }

    public void AssertEnqueued<TJob>()
    {
        if (!_jobs.Any(j => j.JobType == typeof(TJob)))
            throw new InvalidOperationException(
                $"Expected at least one '{typeof(TJob).Name}' job to be enqueued, but none were found.");
    }

    public void AssertEnqueued<TJob, TPayload>(Func<TPayload, bool> predicate)
    {
        var ofType = _jobs.Where(j => j.JobType == typeof(TJob)).ToList();
        if (ofType.Count == 0)
            throw new InvalidOperationException(
                $"Expected at least one '{typeof(TJob).Name}' job to be enqueued, but none were found.");
        if (!ofType.Any(j => predicate((TPayload)j.Payload!)))
            throw new InvalidOperationException(
                $"'{typeof(TJob).Name}' jobs were enqueued, but none matched the predicate.");
    }

    public void AssertNotEnqueued<TJob>()
    {
        var count = _jobs.Count(j => j.JobType == typeof(TJob));
        if (count > 0)
            throw new InvalidOperationException(
                $"Expected no '{typeof(TJob).Name}' jobs to be enqueued, but found {count}.");
    }

    public void AssertEnqueuedCount<TJob>(int expected)
    {
        var actual = _jobs.Count(j => j.JobType == typeof(TJob));
        if (actual != expected)
            throw new InvalidOperationException(
                $"Expected {expected} '{typeof(TJob).Name}' job(s) to be enqueued, but found {actual}.");
    }
}
