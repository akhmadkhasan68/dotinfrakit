using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.EntityFrameworkCore;

namespace DotInfraKit.Queue.Database.Internal;

internal sealed class DatabaseQueueDriver<TContext> : IQueueDriver
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly string _queueName;

    public DatabaseQueueDriver(IDbContextFactory<TContext> factory, string queueName)
    {
        _factory = factory;
        _queueName = queueName;
    }

    public async Task<Guid> EnqueueAsync(QueueJobEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Set<QueueJobRecord>().Add(ToRecord(entry));
        await ctx.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task<QueueJobEntry?> DequeueAsync(string workerId, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            await using var ctx = await _factory.CreateDbContextAsync(ct);

            var candidate = await ctx.Set<QueueJobRecord>()
                .Where(j => j.QueueName == _queueName
                         && j.Status == "pending"
                         && (j.NextRunAt == null || j.NextRunAt <= now))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.NextRunAt)
                .FirstOrDefaultAsync(ct);

            if (candidate is null)
            {
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var claimed = await ctx.Set<QueueJobRecord>()
                .Where(j => j.Id == candidate.Id && j.LockedAt == null && j.Status == "pending")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "processing")
                    .SetProperty(j => j.LockedAt, now)
                    .SetProperty(j => j.LockedBy, workerId), ct);

            if (claimed == 0) continue;

            candidate.Status = "processing";
            candidate.LockedAt = now;
            candidate.LockedBy = workerId;
            return ToEntry(candidate);
        }

        return null;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "completed")
                .SetProperty(j => j.CompletedAt, now), ct);
    }

    public async Task FailAsync(Guid jobId, string error, DateTime? nextRunAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "pending")
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.NextRunAt, nextRunAt)
                .SetProperty(j => j.ErrorMessage, error), ct);
    }

    public async Task MoveToDeadLetterAsync(Guid jobId, string error, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "dead")
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.ErrorMessage, error)
                .SetProperty(j => j.CompletedAt, now), ct);
    }

    public async Task<IReadOnlyList<QueueJobEntry>> GetStuckJobsAsync(DateTime lockedBefore, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var records = await ctx.Set<QueueJobRecord>()
            .Where(j => j.QueueName == _queueName
                     && j.Status == "processing"
                     && j.LockedAt != null
                     && j.LockedAt < lockedBefore)
            .ToListAsync(ct);
        return records.Select(ToEntry).ToList();
    }

    public async Task RequeueStuckJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "pending")
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.NextRunAt, (DateTime?)null), ct);
    }

    public async Task<IReadOnlyList<QueueJobEntry>> GetReadyDelayedJobsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var records = await ctx.Set<QueueJobRecord>()
            .Where(j => j.QueueName == _queueName
                     && j.Status == "pending"
                     && j.NextRunAt != null
                     && j.NextRunAt <= now)
            .ToListAsync(ct);
        return records.Select(ToEntry).ToList();
    }

    public async Task PromoteDelayedJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId && j.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.NextRunAt, (DateTime?)null), ct);
    }

    private static QueueJobEntry ToEntry(QueueJobRecord r) => new()
    {
        Id = r.Id,
        QueueName = r.QueueName,
        JobType = r.JobType,
        Payload = r.Payload,
        Status = r.Status,
        Attempts = r.Attempts,
        MaxAttempts = r.MaxAttempts,
        Priority = r.Priority,
        NextRunAt = r.NextRunAt,
        LockedAt = r.LockedAt,
        LockedBy = r.LockedBy,
        ErrorMessage = r.ErrorMessage,
        CreatedAt = r.CreatedAt,
        CompletedAt = r.CompletedAt
    };

    private static QueueJobRecord ToRecord(QueueJobEntry e) => new()
    {
        Id = e.Id,
        QueueName = e.QueueName,
        JobType = e.JobType,
        Payload = e.Payload,
        Status = e.Status,
        Attempts = e.Attempts,
        MaxAttempts = e.MaxAttempts,
        Priority = e.Priority,
        NextRunAt = e.NextRunAt,
        LockedAt = e.LockedAt,
        LockedBy = e.LockedBy,
        ErrorMessage = e.ErrorMessage,
        CreatedAt = e.CreatedAt,
        CompletedAt = e.CompletedAt
    };
}
