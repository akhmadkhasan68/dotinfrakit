using DotInfraKit.Scheduler;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Testcontainers.MsSql;

namespace DotInfraKit.IntegrationTests;

public sealed class SchedulerClusterModeIntegrationTests : IAsyncLifetime
{
    private MsSqlContainer? _mssql;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        if (!DockerAvailableFactAttribute.IsDockerAvailable()) return;
        _mssql = new MsSqlBuilder().Build();
        await _mssql.StartAsync();
        _connectionString = _mssql.GetConnectionString();
        await ApplyQuartzDdlAsync(_connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_mssql is not null) await _mssql.DisposeAsync();
    }

    private static async Task ApplyQuartzDdlAsync(string connectionString)
    {
        var assembly = typeof(SchedulerClusterModeIntegrationTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "DotInfraKit.IntegrationTests.QuartzSqlServer")
            ?? throw new InvalidOperationException("Quartz DDL embedded resource not found.");

        using var reader = new StreamReader(stream);
        var ddl = await reader.ReadToEndAsync();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class ExecutionCounter
    {
        private int _count;
        public int Count => _count;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Increment()
        {
            Interlocked.Increment(ref _count);
            Done.TrySetResult();
        }
    }

    private sealed class CountingJob(ExecutionCounter counter) : IScheduledJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }

    private IHost BuildHost(ExecutionCounter counter, string instanceId)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(_ => { });
                services.AddSingleton(counter);
                services.AddJobScheduler(scheduler =>
                {
                    scheduler.UseClusterMode(cluster =>
                    {
                        cluster.UseDatabaseStore(_connectionString, QuartzDbProvider.SqlServer);
                        cluster.InstanceId = instanceId;
                    });
                    // Schedule with far-future cron so it doesn't fire automatically
                    scheduler.Schedule<CountingJob>().WithCron("0 0 0 1 1 ? 2099");
                });
            })
            .Build();
    }

    [DockerAvailableFact]
    public async Task SchedulerClusterMode_TwoInstances_JobExecutedExactlyOnce()
    {
        var counter = new ExecutionCounter();

        using var hostA = BuildHost(counter, "node-A");
        using var hostB = BuildHost(counter, "node-B");

        await hostA.StartAsync();
        await hostB.StartAsync();

        // Give schedulers time to register with the cluster store
        await Task.Delay(500);

        // Manually trigger the job from host A's scheduler
        var schedulerFactory = hostA.Services.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(JobKey.Create("CountingJob", "DotInfraKit"));

        // Wait for execution — whichever node wins the trigger fires it once
        await counter.Done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500); // extra window to catch any second execution

        counter.Count.Should().Be(1, "cluster mode must prevent duplicate job execution");

        await hostA.StopAsync();
        await hostB.StopAsync();
    }
}
