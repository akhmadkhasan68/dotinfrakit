using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DotInfraKit.Queue.Internal.Drivers;

internal sealed class MemoryQueueDriver : IQueueDriver
{
    private readonly Channel<QueueJobEntry> _channel;
    private readonly ConcurrentDictionary<Guid, QueueJobEntry> _pending = new();
    private readonly ConcurrentDictionary<Guid, QueueJobEntry> _processing = new();
    private readonly SortedList<DateTime, QueueJobEntry> _delayed = new();
    private readonly object _delayedLock = new();
    private readonly InMemoryDlqStore _dlqStore;

    public MemoryQueueDriver(int channelCapacity, InMemoryDlqStore dlqStore)
    {
        _channel = Channel.CreateBounded<QueueJobEntry>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false
        });
        _dlqStore = dlqStore;
    }

    public async Task<Guid> EnqueueAsync(QueueJobEntry entry, CancellationToken ct = default)
    {
        if (entry.NextRunAt is null || entry.NextRunAt <= DateTime.UtcNow)
        {
            entry.Status = "pending";
            _pending[entry.Id] = entry;
            await _channel.Writer.WriteAsync(entry, ct);
        }
        else
        {
            lock (_delayedLock)
            {
                var key = entry.NextRunAt.Value;
                while (_delayed.ContainsKey(key))
                    key = key.AddTicks(1);
                _delayed[key] = entry;
            }
        }
        return entry.Id;
    }

    public async Task<QueueJobEntry?> DequeueAsync(string workerId, CancellationToken ct = default)
    {
        var entry = await _channel.Reader.ReadAsync(ct);
        _pending.TryRemove(entry.Id, out _);
        entry.Status = "processing";
        entry.LockedAt = DateTime.UtcNow;
        entry.LockedBy = workerId;
        _processing[entry.Id] = entry;
        return entry;
    }

    public Task CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_processing.TryRemove(jobId, out var entry))
        {
            entry.Status = "completed";
            entry.CompletedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public async Task FailAsync(Guid jobId, string error, DateTime? nextRunAt, CancellationToken ct = default)
    {
        if (!_processing.TryRemove(jobId, out var entry))
            return;

        entry.Status = "failed";
        entry.ErrorMessage = error;
        entry.LockedAt = null;
        entry.LockedBy = null;
        entry.NextRunAt = nextRunAt;

        await EnqueueAsync(entry, ct);
    }

    public Task MoveToDeadLetterAsync(Guid jobId, string error, CancellationToken ct = default)
    {
        if (!_processing.TryRemove(jobId, out var entry))
            return Task.CompletedTask;

        entry.Status = "dead";
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
        _dlqStore.Records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueueJobEntry>> GetStuckJobsAsync(DateTime lockedBefore, CancellationToken ct = default)
    {
        var stuck = _processing.Values
            .Where(e => e.LockedAt.HasValue && e.LockedAt.Value < lockedBefore)
            .ToList();
        return Task.FromResult<IReadOnlyList<QueueJobEntry>>(stuck);
    }

    public async Task RequeueStuckJobAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!_processing.TryRemove(jobId, out var entry))
            return;

        entry.Attempts++;
        entry.Status = "pending";
        entry.LockedAt = null;
        entry.LockedBy = null;
        entry.NextRunAt = null;
        _pending[entry.Id] = entry;
        await _channel.Writer.WriteAsync(entry, ct);
    }

    public Task<IReadOnlyList<QueueJobEntry>> GetReadyDelayedJobsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        List<QueueJobEntry> ready;
        lock (_delayedLock)
        {
            ready = _delayed
                .Where(kvp => kvp.Key <= now)
                .Select(kvp => kvp.Value)
                .ToList();
        }
        return Task.FromResult<IReadOnlyList<QueueJobEntry>>(ready);
    }

    public async Task PromoteDelayedJobAsync(Guid jobId, CancellationToken ct = default)
    {
        QueueJobEntry? entry = null;
        lock (_delayedLock)
        {
            var key = _delayed.FirstOrDefault(kvp => kvp.Value.Id == jobId);
            if (key.Value is not null)
            {
                entry = key.Value;
                _delayed.Remove(key.Key);
            }
        }

        if (entry is not null)
        {
            entry.Status = "pending";
            entry.NextRunAt = null;
            _pending[entry.Id] = entry;
            await _channel.Writer.WriteAsync(entry, ct);
        }
    }

    public Task<QueueStats> GetStatsAsync(string queueName, CancellationToken ct = default)
    {
        long delayed;
        lock (_delayedLock) delayed = _delayed.Count;
        return Task.FromResult(new QueueStats(
            queueName,
            _channel.Reader.Count,
            _processing.Count,
            delayed,
            _dlqStore.Records.Count));
    }

    public Task<QueueJobEntry?> GetJobByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_pending.TryGetValue(jobId, out var pending))
            return Task.FromResult<QueueJobEntry?>(pending);

        if (_processing.TryGetValue(jobId, out var processing))
            return Task.FromResult<QueueJobEntry?>(processing);

        lock (_delayedLock)
        {
            var delayed = _delayed.Values.FirstOrDefault(e => e.Id == jobId);
            if (delayed is not null)
                return Task.FromResult<QueueJobEntry?>(delayed);
        }

        if (_dlqStore.Records.TryGetValue(jobId, out var dlq))
            return Task.FromResult<QueueJobEntry?>(new QueueJobEntry
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
            });

        return Task.FromResult<QueueJobEntry?>(null);
    }

    public Task<(IReadOnlyList<QueueJobEntry> Items, long TotalCount)> ListJobsAsync(
        string? status, int skip, int take, CancellationToken ct = default)
    {
        IEnumerable<QueueJobEntry> source;
        if (status == "pending")
            source = _pending.Values;
        else if (status == "processing")
            source = _processing.Values;
        else if (status == "delayed")
        {
            lock (_delayedLock) source = _delayed.Values.ToList();
        }
        else if (status is null)
        {
            List<QueueJobEntry> delayed;
            lock (_delayedLock) delayed = _delayed.Values.ToList();
            source = _pending.Values.Concat(_processing.Values).Concat(delayed);
        }
        else
            source = [];

        var list = source.OrderByDescending(e => e.CreatedAt).ToList();
        var total = list.Count;
        var items = list.Skip(skip).Take(take).ToList();
        return Task.FromResult<(IReadOnlyList<QueueJobEntry>, long)>((items, total));
    }
}
