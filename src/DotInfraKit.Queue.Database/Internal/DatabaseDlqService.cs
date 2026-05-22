using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.EntityFrameworkCore;

namespace DotInfraKit.Queue.Database.Internal;

internal sealed class DatabaseDlqService<TContext> : IDlqService
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly string _queueName;
    private readonly IQueueDriver _driver;

    public DatabaseDlqService(IDbContextFactory<TContext> factory, string queueName, IQueueDriver driver)
    {
        _factory = factory;
        _queueName = queueName;
        _driver = driver;
    }

    public async Task<IReadOnlyList<DlqJobRecord>> GetDeadJobsAsync(string queueName, int page = 1, int pageSize = 20)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var records = await ctx.Set<QueueJobRecord>()
            .Where(j => j.QueueName == queueName && j.Status == "dead")
            .OrderByDescending(j => j.CompletedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return records.Select(r => new DlqJobRecord
        {
            Id = r.Id,
            QueueName = r.QueueName,
            JobType = r.JobType,
            Payload = r.Payload,
            Attempts = r.Attempts,
            ErrorMessage = r.ErrorMessage,
            CreatedAt = r.CreatedAt,
            DeadAt = r.CompletedAt ?? r.CreatedAt
        }).ToList();
    }

    public async Task<Guid> RetryAsync(Guid jobId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId && j.Status == "dead")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "pending")
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.ErrorMessage, (string?)null)
                .SetProperty(j => j.NextRunAt, (DateTime?)null)
                .SetProperty(j => j.CompletedAt, (DateTime?)null));
        return jobId;
    }

    public async Task RetryAllAsync(string queueName)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.QueueName == queueName && j.Status == "dead")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, "pending")
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.LockedAt, (DateTime?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.ErrorMessage, (string?)null)
                .SetProperty(j => j.NextRunAt, (DateTime?)null)
                .SetProperty(j => j.CompletedAt, (DateTime?)null));
    }

    public async Task DeleteAsync(Guid jobId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.Id == jobId && j.Status == "dead")
            .ExecuteDeleteAsync();
    }

    public async Task DeleteAllAsync(string queueName)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Set<QueueJobRecord>()
            .Where(j => j.QueueName == queueName && j.Status == "dead")
            .ExecuteDeleteAsync();
    }
}
