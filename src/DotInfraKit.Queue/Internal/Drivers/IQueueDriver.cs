namespace DotInfraKit.Queue.Internal.Drivers;

internal interface IQueueDriver
{
    Task<Guid> EnqueueAsync(QueueJobEntry entry, CancellationToken ct = default);
    Task<QueueJobEntry?> DequeueAsync(string workerId, CancellationToken ct = default);
    Task CompleteAsync(Guid jobId, CancellationToken ct = default);
    Task FailAsync(Guid jobId, string error, DateTime? nextRunAt, CancellationToken ct = default);
    Task MoveToDeadLetterAsync(Guid jobId, string error, CancellationToken ct = default);
    Task<IReadOnlyList<QueueJobEntry>> GetStuckJobsAsync(DateTime lockedBefore, CancellationToken ct = default);
    Task RequeueStuckJobAsync(Guid jobId, CancellationToken ct = default);
    Task<IReadOnlyList<QueueJobEntry>> GetReadyDelayedJobsAsync(CancellationToken ct = default);
    Task PromoteDelayedJobAsync(Guid jobId, CancellationToken ct = default);
    Task<QueueStats> GetStatsAsync(string queueName, CancellationToken ct = default);
    Task<QueueJobEntry?> GetJobByIdAsync(Guid jobId, CancellationToken ct = default);
    Task<(IReadOnlyList<QueueJobEntry> Items, long TotalCount)> ListJobsAsync(
        string? status, int skip, int take, CancellationToken ct = default);
}
