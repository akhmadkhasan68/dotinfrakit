namespace DotInfraKit.Queue;

public interface IQueueMonitorService
{
    IReadOnlyList<QueueInfo> GetQueues();
    Task<IReadOnlyList<QueueStats>> GetAllStatsAsync(CancellationToken ct = default);
    Task<QueueJobDetail?> GetJobAsync(string queueName, Guid jobId, CancellationToken ct = default);
    Task<(IReadOnlyList<QueueJobDetail> Items, long TotalCount)> ListJobsAsync(
        string? queueName, string? status, int page, int pageSize, CancellationToken ct = default);
}
