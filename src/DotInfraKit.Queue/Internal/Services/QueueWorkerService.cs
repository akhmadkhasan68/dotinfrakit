using System.Text.Json;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class QueueWorkerService : BackgroundService
{
    private readonly IQueueDriver _driver;
    private readonly QueueConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueueWorkerService> _logger;
    private readonly string _workerId;
    private readonly int _totalWorkerCount;

    public QueueWorkerService(
        IQueueDriver driver,
        QueueConfiguration config,
        IServiceScopeFactory scopeFactory,
        ILogger<QueueWorkerService> logger,
        string workerId,
        int totalWorkerCount = 1)
    {
        _driver = driver;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = workerId;
        _totalWorkerCount = totalWorkerCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_totalWorkerCount > 1 && _driver is MemoryQueueDriver)
            _logger.LogWarning(
                "Queue '{Queue}' has {Count} workers but uses the in-memory driver, which is single-process. Extra workers are redundant.",
                _config.QueueName, _totalWorkerCount);

        using var semaphore = new SemaphoreSlim(_config.Concurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            await semaphore.WaitAsync(stoppingToken);

            QueueJobEntry? entry;
            try
            {
                entry = await _driver.DequeueAsync(_workerId, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                semaphore.Release();
                break;
            }

            if (entry is null)
            {
                semaphore.Release();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(entry, stoppingToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(QueueJobEntry entry, CancellationToken ct)
    {
        entry.Attempts++;
        var context = new JobContext
        {
            JobId = entry.Id,
            QueueName = entry.QueueName,
            AttemptNumber = entry.Attempts,
            MaxAttempts = entry.MaxAttempts,
            EnqueuedAt = entry.CreatedAt
        };

        try
        {
            var jobType = Type.GetType(entry.JobType)
                ?? throw new InvalidOperationException($"Cannot resolve job type '{entry.JobType}'.");

            var payloadType = GetPayloadType(jobType)
                ?? throw new InvalidOperationException($"Cannot determine payload type for job '{jobType.Name}'.");

            var payload = JsonSerializer.Deserialize(entry.Payload, payloadType)
                ?? throw new InvalidOperationException($"Deserialized payload is null for job '{jobType.Name}'.");

            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService(jobType);
            var method = jobType.GetMethod("ExecuteAsync")!;
            await (Task)method.Invoke(job, [payload, context, ct])!;
            await _driver.CompleteAsync(entry.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Exception actual = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? inner
                : ex;
            _logger.LogError(actual, "Job {JobId} ({JobType}) failed on attempt {Attempt}/{Max}.", entry.Id, entry.JobType, entry.Attempts, entry.MaxAttempts);
            await MoveToDeadOrFail(entry, actual.Message);
        }
    }

    private async Task MoveToDeadOrFail(QueueJobEntry entry, string error)
    {
        if (entry.Attempts >= entry.MaxAttempts)
        {
            if (_config.DlqEnabled)
                await _driver.MoveToDeadLetterAsync(entry.Id, error, CancellationToken.None);
            else
            {
                _logger.LogWarning("Job {JobId} ({JobType}) exceeded max attempts and DLQ is disabled. Discarding.", entry.Id, entry.JobType);
                await _driver.CompleteAsync(entry.Id, CancellationToken.None);
            }
        }
        else
        {
            var nextRunAt = DateTime.UtcNow + _config.RetryPolicy.CalculateDelay(entry.Attempts);
            await _driver.FailAsync(entry.Id, error, nextRunAt, CancellationToken.None);
        }
    }

    private static Type? GetPayloadType(Type jobType)
    {
        var iface = jobType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueueJob<>));
        return iface?.GetGenericArguments()[0];
    }
}
