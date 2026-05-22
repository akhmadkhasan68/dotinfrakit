using DotInfraKit.Queue;
using DotInfraKit.Queue.Redis;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;

namespace DotInfraKit.IntegrationTests;

public sealed class RedisQueueIntegrationTests : IAsyncLifetime
{
    private RedisContainer? _redis;

    public async Task InitializeAsync()
    {
        if (!DockerAvailableFactAttribute.IsDockerAvailable()) return;
        _redis = new RedisBuilder().Build();
        await _redis.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null) await _redis.DisposeAsync();
    }

    private sealed record TestPayload(string Email, string Name);

    private sealed class CapturingJob : IQueueJob<TestPayload>
    {
        private readonly TaskCompletionSource<TestPayload> _tcs;
        private readonly Exception? _throwOnExecute;
        private int _callCount;

        public CapturingJob(TaskCompletionSource<TestPayload> tcs, Exception? throwOnExecute = null)
        {
            _tcs = tcs;
            _throwOnExecute = throwOnExecute;
        }

        public int CallCount => _callCount;
        public Task WaitForExecutionAsync() => _tcs.Task;

        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_throwOnExecute is not null)
            {
                _tcs.TrySetResult(payload);
                throw _throwOnExecute;
            }
            _tcs.TrySetResult(payload);
            return Task.CompletedTask;
        }
    }

    private IHost BuildHost(CapturingJob job, int maxAttempts = 3, int initialDelayMs = 50)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(_ => { });
                services.AddSingleton(job);
                services.AddJobQueue(options =>
                    options.UseDefaultQueue(q =>
                    {
                        q.UseRedis(r => r.Endpoint = _redis!.GetConnectionString());
                        q.Workers(concurrency: 1);
                        q.Retry(maxAttempts: maxAttempts, BackoffType.Fixed, initialDelayMs: initialDelayMs);
                        q.EnableDeadLetterQueue();
                    }));
            })
            .Build();
    }

    [DockerAvailableFact]
    public async Task EnqueueAsync_JobIsPickedUpAndExecuted()
    {
        var tcs = new TaskCompletionSource<TestPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new CapturingJob(tcs);
        using var host = BuildHost(job);
        await host.StartAsync();

        var queue = host.Services.GetRequiredService<IQueueService>();
        await queue.EnqueueAsync<CapturingJob, TestPayload>(new TestPayload("test@example.com", "Alice"));

        var payload = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        payload.Email.Should().Be("test@example.com");
        payload.Name.Should().Be("Alice");
        await host.StopAsync();
    }

    [DockerAvailableFact]
    public async Task FailingJob_IsRetriedAndMovedToDlq()
    {
        var tcs = new TaskCompletionSource<TestPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new CapturingJob(tcs, throwOnExecute: new InvalidOperationException("simulated failure"));
        using var host = BuildHost(job, maxAttempts: 1, initialDelayMs: 0);
        await host.StartAsync();

        var queue = host.Services.GetRequiredService<IQueueService>();
        await queue.EnqueueAsync<CapturingJob, TestPayload>(new TestPayload("dead@example.com", "Bob"));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500); // allow DLQ write to complete

        var dlq = host.Services.GetRequiredService<IDlqService>();
        var dead = await dlq.GetDeadJobsAsync("default");

        dead.Should().HaveCount(1);
        dead[0].ErrorMessage.Should().Contain("simulated failure");
        await host.StopAsync();
    }
}
