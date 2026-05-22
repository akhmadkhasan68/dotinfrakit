using System.Text.Json;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.Extensions.Logging;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class QueueService : IQueueService
{
    private readonly IReadOnlyDictionary<string, (IQueueDriver Driver, QueueConfiguration Config)> _queues;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        IReadOnlyDictionary<string, (IQueueDriver Driver, QueueConfiguration Config)> queues,
        ILogger<QueueService> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    public Task<Guid> EnqueueAsync<TJob, TPayload>(TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>
        => EnqueueAsync<TJob, TPayload>("default", payload, options);

    public Task<Guid> EnqueueAsync<TJob, TPayload>(string queueName, TPayload payload, EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>
    {
        if (!_queues.TryGetValue(queueName, out var tuple))
            throw new InvalidOperationException($"Queue '{queueName}' is not registered.");

        var (driver, config) = tuple;

        if (options?.Priority > 0)
            _logger.LogWarning("Priority > 0 is not supported by the memory driver for queue '{Queue}'. Job will be enqueued without priority ordering.", queueName);

        DateTime? nextRunAt = options?.RunAt
            ?? (options?.Delay.HasValue == true ? DateTime.UtcNow + options.Delay.Value : null);

        var entry = new QueueJobEntry
        {
            QueueName = queueName,
            JobType = typeof(TJob).AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(payload),
            MaxAttempts = config.MaxAttempts,
            Priority = options?.Priority ?? 0,
            NextRunAt = nextRunAt
        };

        return driver.EnqueueAsync(entry);
    }
}
