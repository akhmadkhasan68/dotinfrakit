using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotInfraKit.Queue.Internal.Services;

internal sealed class DelayedJobSweeperService : BackgroundService
{
    private readonly IQueueDriver _driver;
    private readonly QueueConfiguration _config;
    private readonly ILogger<DelayedJobSweeperService> _logger;

    public DelayedJobSweeperService(
        IQueueDriver driver,
        QueueConfiguration config,
        ILogger<DelayedJobSweeperService> logger)
    {
        _driver = driver;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.DelayedJobPollingInterval, stoppingToken);
            await PromoteReadyJobsAsync(stoppingToken);
        }
    }

    private async Task PromoteReadyJobsAsync(CancellationToken ct)
    {
        var ready = await _driver.GetReadyDelayedJobsAsync(ct);
        foreach (var entry in ready)
        {
            _logger.LogDebug("Promoting delayed job {JobId} ({JobType}) to queue '{Queue}'.", entry.Id, entry.JobType, entry.QueueName);
            await _driver.PromoteDelayedJobAsync(entry.Id, ct);
        }
    }
}
