using DotInfraKit.Queue.Internal.Drivers;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class InMemoryDlqService : IDlqService
{
    private readonly InMemoryDlqStore _store;
    private readonly IQueueDriver _driver;

    public InMemoryDlqService(InMemoryDlqStore store, IQueueDriver driver)
    {
        _store = store;
        _driver = driver;
    }

    public Task<IReadOnlyList<DlqJobRecord>> GetDeadJobsAsync(string queueName, int page = 1, int pageSize = 20)
    {
        var records = _store.Records.Values
            .Where(r => r.QueueName == queueName)
            .OrderByDescending(r => r.DeadAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return Task.FromResult<IReadOnlyList<DlqJobRecord>>(records);
    }

    public async Task<Guid> RetryAsync(Guid jobId)
    {
        if (!_store.Records.TryGetValue(jobId, out var record))
            throw new InvalidOperationException($"DLQ job '{jobId}' not found.");

        var entry = new QueueJobEntry
        {
            QueueName = record.QueueName,
            JobType = record.JobType,
            Payload = record.Payload,
            Attempts = 0
        };

        var newId = await _driver.EnqueueAsync(entry);
        _store.Records.TryRemove(jobId, out _);
        return newId;
    }

    public async Task RetryAllAsync(string queueName)
    {
        var records = _store.Records.Values
            .Where(r => r.QueueName == queueName)
            .ToList();

        foreach (var record in records)
            await RetryAsync(record.Id);
    }

    public Task DeleteAsync(Guid jobId)
    {
        _store.Records.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(string queueName)
    {
        var toRemove = _store.Records.Values
            .Where(r => r.QueueName == queueName)
            .Select(r => r.Id)
            .ToList();

        foreach (var id in toRemove)
            _store.Records.TryRemove(id, out _);

        return Task.CompletedTask;
    }
}
