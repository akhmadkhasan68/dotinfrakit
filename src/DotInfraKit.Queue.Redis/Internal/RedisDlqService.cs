using System.Text.Json;
using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Drivers;
using StackExchange.Redis;

namespace DotInfraKit.Queue.Redis.Internal;

internal sealed class RedisDlqService : IDlqService
{
    private readonly IDatabase _db;
    private readonly string _dlqKey;
    private readonly IQueueDriver _driver;

    public RedisDlqService(IDatabase db, string keyPrefix, string queueName, IQueueDriver driver)
    {
        _db = db;
        _dlqKey = $"{keyPrefix}dlq:{queueName}";
        _driver = driver;
    }

    public async Task<IReadOnlyList<DlqJobRecord>> GetDeadJobsAsync(string queueName, int page = 1, int pageSize = 20)
    {
        var all = await _db.HashGetAllAsync(_dlqKey);
        return all
            .Select(e => JsonSerializer.Deserialize<DlqJobRecord>((string)e.Value!)!)
            .Where(r => r.QueueName == queueName)
            .OrderByDescending(r => r.DeadAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    public async Task<Guid> RetryAsync(Guid jobId)
    {
        var value = await _db.HashGetAsync(_dlqKey, jobId.ToString("N"));
        if (value.IsNull)
            throw new InvalidOperationException($"DLQ job '{jobId}' not found.");

        var record = JsonSerializer.Deserialize<DlqJobRecord>((string)value!)!;
        var entry = new QueueJobEntry
        {
            QueueName = record.QueueName,
            JobType = record.JobType,
            Payload = record.Payload,
            Attempts = 0
        };

        var newId = await _driver.EnqueueAsync(entry);
        await _db.HashDeleteAsync(_dlqKey, jobId.ToString("N"));
        return newId;
    }

    public async Task RetryAllAsync(string queueName)
    {
        var all = await _db.HashGetAllAsync(_dlqKey);
        var matching = all
            .Select(e => JsonSerializer.Deserialize<DlqJobRecord>((string)e.Value!)!)
            .Where(r => r.QueueName == queueName)
            .ToList();

        foreach (var record in matching)
            await RetryAsync(record.Id);
    }

    public Task DeleteAsync(Guid jobId) =>
        _db.HashDeleteAsync(_dlqKey, jobId.ToString("N"));

    public Task DeleteAllAsync(string queueName) =>
        _db.KeyDeleteAsync(_dlqKey);
}
