using DotInfraKit.Queue;
using DotInfraKit.Queue.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotInfraKit.IntegrationTests;

public sealed class DatabaseQueueIntegrationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dotinfrakit-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        // Create schema before any host starts to avoid race between migration and worker
        var opts = new DbContextOptionsBuilder<IntegrationTestDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var ctx = new IntegrationTestDbContext(opts);
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private sealed record TestPayload(string Value);

    private sealed class CapturingJob : IQueueJob<TestPayload>
    {
        private readonly TaskCompletionSource<TestPayload> _tcs;
        private int _executeCount;

        public CapturingJob(TaskCompletionSource<TestPayload> tcs)
            => _tcs = tcs;

        public int ExecuteCount => _executeCount;

        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            _tcs.TrySetResult(payload);
            return Task.CompletedTask;
        }
    }

    private IHost BuildHost(CapturingJob job, string? connectionString = null)
    {
        var cs = connectionString ?? _connectionString;
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(_ => { });
                services.AddSingleton(job);
                services.AddDbContextFactory<IntegrationTestDbContext>(opts =>
                    opts.UseSqlite(cs));
                services.AddJobQueue(options =>
                    options.UseDefaultQueue(q =>
                    {
                        q.UseDatabaseDriver<IntegrationTestDbContext>();
                        q.Workers(concurrency: 1);
                        q.Retry(maxAttempts: 3, BackoffType.Fixed, initialDelayMs: 100);
                    }));
            })
            .Build();
    }

    [Fact]
    public async Task DatabaseDriver_PersistsJobAcrossRestart()
    {
        var tcs = new TaskCompletionSource<TestPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job1 = new CapturingJob(tcs);

        // Start first host, enqueue with far-future RunAt so worker won't process it before shutdown
        using (var host1 = BuildHost(job1))
        {
            await host1.StartAsync();
            var queue = host1.Services.GetRequiredService<IQueueService>();
            await queue.EnqueueAsync<CapturingJob, TestPayload>(
                new TestPayload("persist-test"),
                new EnqueueOptions { RunAt = DateTime.UtcNow.AddDays(1) });
            await host1.StopAsync();
        }

        job1.ExecuteCount.Should().Be(0, "job should not have been processed before shutdown");

        // Clear next_run_at so the job is immediately eligible for dequeue (avoids datetime format issues)
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE dotinfrakit_queue_jobs SET next_run_at = NULL WHERE status = 'pending'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Start second host pointing to same DB — job should be picked up
        var tcs2 = new TaskCompletionSource<TestPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job2 = new CapturingJob(tcs2);
        using var host2 = BuildHost(job2);
        await host2.StartAsync();

        var payload = await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(20));
        payload.Value.Should().Be("persist-test");
        await host2.StopAsync();
    }

    [Fact]
    public async Task DatabaseDriver_TwoInstances_DoNotProcessSameJob()
    {
        var tcs = new TaskCompletionSource<TestPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedJob = new CapturingJob(tcs);

        // Build two hosts sharing the same SQLite database
        using var hostA = BuildHost(sharedJob);
        using var hostB = BuildHost(sharedJob);

        await hostA.StartAsync();
        await hostB.StartAsync();

        var queue = hostA.Services.GetRequiredService<IQueueService>();
        await queue.EnqueueAsync<CapturingJob, TestPayload>(new TestPayload("once-only"));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500); // extra window for any second execution

        sharedJob.ExecuteCount.Should().Be(1, "optimistic locking must prevent duplicate execution");

        await hostA.StopAsync();
        await hostB.StopAsync();
    }
}
