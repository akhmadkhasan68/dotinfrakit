using System.Text.Json;
using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Drivers;
using StackExchange.Redis;

namespace DotInfraKit.Queue.Redis.Internal;

internal sealed class RedisQueueDriver : IQueueDriver
{
    private readonly IDatabase _db;
    private readonly string _prefix;
    private readonly string _queueName;

    private string QueueKey => $"{_prefix}queue:{_queueName}";
    private string DelayedKey => $"{_prefix}delayed:{_queueName}";
    private string ProcessingKey => $"{_prefix}processing:{_queueName}";
    private string JobKey(Guid id) => $"{_prefix}job:{id:N}";

    public RedisQueueDriver(IDatabase db, string keyPrefix, string queueName)
    {
        _db = db;
        _prefix = keyPrefix;
        _queueName = queueName;
    }

    public async Task<Guid> EnqueueAsync(QueueJobEntry entry, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(entry);
        await _db.StringSetAsync(JobKey(entry.Id), json);

        if (entry.NextRunAt is null || entry.NextRunAt <= DateTime.UtcNow)
        {
            entry.Status = "pending";
            await _db.ListRightPushAsync(QueueKey, entry.Id.ToString("N"));
        }
        else
        {
            var score = ToEpoch(entry.NextRunAt.Value);
            await _db.SortedSetAddAsync(DelayedKey, entry.Id.ToString("N"), score);
        }

        return entry.Id;
    }

    public async Task<QueueJobEntry?> DequeueAsync(string workerId, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var value = await _db.ListLeftPopAsync(QueueKey);
            if (!value.IsNull)
            {
                var jobId = ParseGuid((string)value!);
                var json = await _db.StringGetAsync(JobKey(jobId));
                if (json.IsNull) continue;

                var entry = JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!;
                entry.Status = "processing";
                entry.LockedAt = DateTime.UtcNow;
                entry.LockedBy = workerId;

                await _db.StringSetAsync(JobKey(jobId), JsonSerializer.Serialize(entry));
                await _db.SortedSetAddAsync(ProcessingKey, jobId.ToString("N"), ToEpoch(entry.LockedAt.Value));

                return entry;
            }

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
        return null;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        await _db.SortedSetRemoveAsync(ProcessingKey, jobId.ToString("N"));
        await _db.KeyDeleteAsync(JobKey(jobId));
    }

    public async Task FailAsync(Guid jobId, string error, DateTime? nextRunAt, CancellationToken ct = default)
    {
        await _db.SortedSetRemoveAsync(ProcessingKey, jobId.ToString("N"));

        var json = await _db.StringGetAsync(JobKey(jobId));
        if (json.IsNull) return;

        var entry = JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!;
        entry.Attempts++;
        entry.Status = "failed";
        entry.ErrorMessage = error;
        entry.LockedAt = null;
        entry.LockedBy = null;
        entry.NextRunAt = nextRunAt;

        await _db.StringSetAsync(JobKey(jobId), JsonSerializer.Serialize(entry));

        if (nextRunAt is null || nextRunAt <= DateTime.UtcNow)
            await _db.ListRightPushAsync(QueueKey, jobId.ToString("N"));
        else
            await _db.SortedSetAddAsync(DelayedKey, jobId.ToString("N"), ToEpoch(nextRunAt.Value));
    }

    public async Task MoveToDeadLetterAsync(Guid jobId, string error, CancellationToken ct = default)
    {
        await _db.SortedSetRemoveAsync(ProcessingKey, jobId.ToString("N"));

        var json = await _db.StringGetAsync(JobKey(jobId));
        if (json.IsNull) return;

        var entry = JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!;
        var record = new DlqJobRecord
        {
            Id = entry.Id,
            QueueName = entry.QueueName,
            JobType = entry.JobType,
            Payload = entry.Payload,
            Attempts = entry.Attempts,
            ErrorMessage = error,
            CreatedAt = entry.CreatedAt,
            DeadAt = DateTime.UtcNow
        };

        var dlqKey = $"{_prefix}dlq:{_queueName}";
        await _db.HashSetAsync(dlqKey, jobId.ToString("N"), JsonSerializer.Serialize(record));
        await _db.KeyDeleteAsync(JobKey(jobId));
    }

    public async Task<IReadOnlyList<QueueJobEntry>> GetStuckJobsAsync(DateTime lockedBefore, CancellationToken ct = default)
    {
        var members = await _db.SortedSetRangeByScoreAsync(ProcessingKey, 0, ToEpoch(lockedBefore));
        return await LoadEntries(members);
    }

    public async Task RequeueStuckJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await _db.SortedSetRemoveAsync(ProcessingKey, jobId.ToString("N"));

        var json = await _db.StringGetAsync(JobKey(jobId));
        if (json.IsNull) return;

        var entry = JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!;
        entry.Attempts++;
        entry.Status = "pending";
        entry.LockedAt = null;
        entry.LockedBy = null;
        entry.NextRunAt = null;

        await _db.StringSetAsync(JobKey(jobId), JsonSerializer.Serialize(entry));
        await _db.ListRightPushAsync(QueueKey, jobId.ToString("N"));
    }

    public async Task<IReadOnlyList<QueueJobEntry>> GetReadyDelayedJobsAsync(CancellationToken ct = default)
    {
        var members = await _db.SortedSetRangeByScoreAsync(DelayedKey, 0, ToEpoch(DateTime.UtcNow));
        return await LoadEntries(members);
    }

    public async Task PromoteDelayedJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await _db.SortedSetRemoveAsync(DelayedKey, jobId.ToString("N"));

        var json = await _db.StringGetAsync(JobKey(jobId));
        if (json.IsNull) return;

        var entry = JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!;
        entry.Status = "pending";
        entry.NextRunAt = null;

        await _db.StringSetAsync(JobKey(jobId), JsonSerializer.Serialize(entry));
        await _db.ListRightPushAsync(QueueKey, jobId.ToString("N"));
    }

    private async Task<IReadOnlyList<QueueJobEntry>> LoadEntries(RedisValue[] members)
    {
        if (members.Length == 0) return [];

        var tasks = members.Select(m => _db.StringGetAsync(JobKey(ParseGuid((string)m!))));
        var jsons = await Task.WhenAll(tasks);

        var result = new List<QueueJobEntry>(jsons.Length);
        foreach (var json in jsons)
        {
            if (!json.IsNull)
                result.Add(JsonSerializer.Deserialize<QueueJobEntry>((string)json!)!);
        }
        return result;
    }

    private static double ToEpoch(DateTime dt) =>
        (dt.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;

    private static Guid ParseGuid(string value) =>
        Guid.ParseExact(value, "N");
}
