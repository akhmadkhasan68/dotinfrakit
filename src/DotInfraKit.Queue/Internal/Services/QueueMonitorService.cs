using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class QueueMonitorService : IQueueMonitorService
{
    private readonly IServiceProvider _sp;
    private readonly QueueRegistry _registry;

    public QueueMonitorService(IServiceProvider sp, QueueRegistry registry)
    {
        _sp = sp;
        _registry = registry;
    }

    public IReadOnlyList<QueueInfo> GetQueues() => _registry.ToQueueInfoList();

    public async Task<IReadOnlyList<QueueStats>> GetAllStatsAsync(CancellationToken ct = default)
    {
        var result = new List<QueueStats>(_registry.Names.Count);
        foreach (var name in _registry.Names)
        {
            var driver = _sp.GetRequiredKeyedService<IQueueDriver>(name);
            result.Add(await driver.GetStatsAsync(name, ct));
        }
        return result;
    }

    public async Task<QueueJobDetail?> GetJobAsync(string queueName, Guid jobId, CancellationToken ct = default)
    {
        if (_registry.GetConfig(queueName) is null)
            return null;

        var driver = _sp.GetRequiredKeyedService<IQueueDriver>(queueName);
        var entry = await driver.GetJobByIdAsync(jobId, ct);
        return entry is null ? null : ToDetail(entry);
    }

    public async Task<(IReadOnlyList<QueueJobDetail> Items, long TotalCount)> ListJobsAsync(
        string? queueName, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var skip = (page - 1) * pageSize;

        if (!string.IsNullOrWhiteSpace(queueName))
        {
            if (_registry.GetConfig(queueName) is null)
                return ([], 0L);

            var driver = _sp.GetRequiredKeyedService<IQueueDriver>(queueName);
            var (entries, total) = await driver.ListJobsAsync(status, skip, pageSize, ct);
            return (entries.Select(ToDetail).ToList(), total);
        }

        var allEntries = new List<QueueJobEntry>();
        foreach (var name in _registry.Names)
        {
            var driver = _sp.GetRequiredKeyedService<IQueueDriver>(name);
            var (entries, _) = await driver.ListJobsAsync(status, 0, int.MaxValue, ct);
            allEntries.AddRange(entries);
        }

        var sorted = allEntries.OrderByDescending(e => e.CreatedAt).ToList();
        return (sorted.Skip(skip).Take(pageSize).Select(ToDetail).ToList(), sorted.Count);
    }

    private static QueueJobDetail ToDetail(QueueJobEntry entry) => new(
        entry.Id,
        entry.QueueName,
        entry.JobType,
        entry.Payload,
        entry.Status,
        entry.Attempts,
        entry.MaxAttempts,
        entry.Priority,
        entry.NextRunAt,
        entry.LockedAt,
        entry.LockedBy,
        entry.ErrorMessage,
        entry.CreatedAt,
        entry.CompletedAt);
}
