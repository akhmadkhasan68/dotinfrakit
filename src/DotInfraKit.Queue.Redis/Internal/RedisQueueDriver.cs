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
    private string DlqKey => $"{_prefix}dlq:{_queueName}";
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
            MaxAttempts = entry.MaxAttempts,
            ErrorMessage = error,
            CreatedAt = entry.CreatedAt,
            DeadAt = DateTime.UtcNow
        };

        await _db.HashSetAsync(DlqKey, jobId.ToString("N"), JsonSerializer.Serialize(record));
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

    public async Task<QueueStats> GetStatsAsync(string queueName, CancellationToken ct = default)
    {
        var pending    = await _db.ListLengthAsync(QueueKey);
        var processing = await _db.SortedSetLengthAsync(ProcessingKey);
        var delayed    = await _db.SortedSetLengthAsync(DelayedKey);
        var dlq        = await _db.HashLengthAsync(DlqKey);
        return new QueueStats(queueName, pending, processing, delayed, dlq);
    }

    public async Task<QueueJobEntry?> GetJobByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(JobKey(jobId));
        if (json.HasValue)
            return JsonSerializer.Deserialize<QueueJobEntry>((string)json!);

        var dlqJson = await _db.HashGetAsync(DlqKey, jobId.ToString("N"));
        if (!dlqJson.HasValue) return null;

        var dlq = JsonSerializer.Deserialize<DlqJobRecord>((string)dlqJson!)!;
        return new QueueJobEntry
        {
            Id = dlq.Id,
            QueueName = dlq.QueueName,
            JobType = dlq.JobType,
            Payload = dlq.Payload,
            Status = "dead",
            Attempts = dlq.Attempts,
            MaxAttempts = dlq.MaxAttempts,
            ErrorMessage = dlq.ErrorMessage,
            CreatedAt = dlq.CreatedAt,
            CompletedAt = dlq.DeadAt
        };
    }

    public async Task<(IReadOnlyList<QueueJobEntry> Items, long TotalCount)> ListJobsAsync(
        string? status, int skip, int take, CancellationToken ct = default)
    {
        return status switch
        {
            "pending"    => await ListFromListAsync(QueueKey, skip, take),
            "processing" => await ListFromZSetAsync(ProcessingKey, skip, take),
            "delayed"    => await ListFromZSetAsync(DelayedKey, skip, take),
            "dead"       => await ListDeadAsync(skip, take),
            null         => await ListAllAsync(skip, take),
            _            => ([], 0L)
        };
    }

    private async Task<(IReadOnlyList<QueueJobEntry>, long)> ListFromListAsync(string key, int skip, int take)
    {
        var total = await _db.ListLengthAsync(key);
        if (total == 0) return ([], 0L);
        var members = await _db.ListRangeAsync(key, skip, skip + take - 1);
        var items = await LoadEntries(members);
        return (items, total);
    }

    private async Task<(IReadOnlyList<QueueJobEntry>, long)> ListFromZSetAsync(string key, int skip, int take)
    {
        var total = await _db.SortedSetLengthAsync(key);
        if (total == 0) return ([], 0L);
        var members = await _db.SortedSetRangeByRankAsync(key, skip, skip + take - 1);
        var items = await LoadEntries(members);
        return (items, total);
    }

    private async Task<(IReadOnlyList<QueueJobEntry>, long)> ListDeadAsync(int skip, int take)
    {
        var total = await _db.HashLengthAsync(DlqKey);
        if (total == 0) return ([], 0L);
        var all = await _db.HashGetAllAsync(DlqKey);
        var entries = all
            .Select(e => JsonSerializer.Deserialize<DlqJobRecord>((string)e.Value!)!)
            .Select(r => new QueueJobEntry
            {
                Id = r.Id,
                QueueName = r.QueueName,
                JobType = r.JobType,
                Payload = r.Payload,
                Status = "dead",
                Attempts = r.Attempts,
                MaxAttempts = r.MaxAttempts,
                ErrorMessage = r.ErrorMessage,
                CreatedAt = r.CreatedAt
            })
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip).Take(take)
            .ToList();
        return (entries, total);
    }

    private async Task<(IReadOnlyList<QueueJobEntry>, long)> ListAllAsync(int skip, int take)
    {
        var (pending,    _) = await ListFromListAsync(QueueKey, 0, int.MaxValue);
        var (processing, _) = await ListFromZSetAsync(ProcessingKey, 0, int.MaxValue);
        var (delayed,    _) = await ListFromZSetAsync(DelayedKey, 0, int.MaxValue);
        var (dead,       _) = await ListDeadAsync(0, int.MaxValue);

        var all = pending.Concat(processing).Concat(delayed).Concat(dead)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
        var total = all.Count;
        return (all.Skip(skip).Take(take).ToList(), total);
    }

    private static double ToEpoch(DateTime dt) =>
        (dt.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;

    private static Guid ParseGuid(string value) =>
        Guid.ParseExact(value, "N");
}
