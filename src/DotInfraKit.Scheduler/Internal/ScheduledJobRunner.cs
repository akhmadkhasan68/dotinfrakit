using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace DotInfraKit.Scheduler.Internal;

[DisallowConcurrentExecution]
internal sealed class ScheduledJobRunner : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScheduledJobRunner> _logger;

    public ScheduledJobRunner(IServiceProvider serviceProvider, ILogger<ScheduledJobRunner> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var typeName = context.JobDetail.JobDataMap.GetString("JobType")
            ?? throw new InvalidOperationException("JobType key not found in Quartz JobDataMap.");

        var jobType = Type.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"Job type '{typeName}' could not be resolved. Ensure the job class is registered in DI.");

        var job = (IScheduledJob)_serviceProvider.GetRequiredService(jobType);
        var handler = _serviceProvider.GetService<IScheduledJobExceptionHandler>();

        try
        {
            await job.ExecuteAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled job {JobType} failed", jobType.Name);

            if (handler is not null)
                await handler.HandleAsync(jobType, ex, context.CancellationToken);
        }
    }
}
