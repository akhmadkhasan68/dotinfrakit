using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class StuckJobSweeperService : BackgroundService
{
    private readonly IQueueDriver _driver;
    private readonly QueueConfiguration _config;
    private readonly ILogger<StuckJobSweeperService> _logger;

    public StuckJobSweeperService(
        IQueueDriver driver,
        QueueConfiguration config,
        ILogger<StuckJobSweeperService> logger)
    {
        _driver = driver;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _config.LockTimeout / 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            await SweepAsync(stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var lockedBefore = DateTime.UtcNow - _config.LockTimeout;
        var stuck = await _driver.GetStuckJobsAsync(lockedBefore, ct);

        foreach (var entry in stuck)
        {
            if (entry.Attempts >= _config.MaxAttempts)
            {
                if (_config.DlqEnabled)
                    await _driver.MoveToDeadLetterAsync(entry.Id, "Job timed out and exceeded max attempts.", ct);
                else
                    _logger.LogWarning("Stuck job {JobId} ({JobType}) exceeded max attempts and DLQ is disabled. Discarding.", entry.Id, entry.JobType);
            }
            else
            {
                _logger.LogWarning("Requeuing stuck job {JobId} ({JobType}), attempt {Attempt}/{Max}.", entry.Id, entry.JobType, entry.Attempts + 1, _config.MaxAttempts);
                await _driver.RequeueStuckJobAsync(entry.Id, ct);
            }
        }
    }
}
