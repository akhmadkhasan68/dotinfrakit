using System.Text.Json;
using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using DotInfraKit.Queue.Internal.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotInfraKit.Queue.Tests;

public class QueueWorkerServiceTests
{
    private sealed class TestPayload
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class CapturingJob : IQueueJob<TestPayload>
    {
        public TestPayload? ReceivedPayload { get; private set; }
        public JobContext? ReceivedContext { get; private set; }
        public bool WasCalled => ReceivedPayload is not null;
        public Exception? ThrowOnExecute { get; set; }

        private readonly TaskCompletionSource<bool> _executedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForExecutionAsync() => _executedTcs.Task;

        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
        {
            ReceivedPayload = payload;
            ReceivedContext = context;
            _executedTcs.TrySetResult(true);
            if (ThrowOnExecute is not null) throw ThrowOnExecute;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
    }

    private (MemoryQueueDriver Driver, InMemoryDlqStore DlqStore, QueueWorkerService Worker, CapturingJob Job)
        BuildSystem(int maxAttempts = 3, bool dlqEnabled = false, int initialDelayMs = 0)
    {
        var job = new CapturingJob();
        var dlqStore = new InMemoryDlqStore();
        var driver = new MemoryQueueDriver(50, dlqStore);

        var config = new QueueConfiguration
        {
            QueueName = "test",
            MaxAttempts = maxAttempts,
            Concurrency = 1,
            DlqEnabled = dlqEnabled,
            RetryPolicy = new RetryPolicy { BackoffType = BackoffType.Fixed, InitialDelayMs = initialDelayMs }
        };

        var services = new ServiceCollection();
        services.AddSingleton(job);
        var sp = services.BuildServiceProvider();

        var worker = new QueueWorkerService(
            driver,
            config,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueueWorkerService>.Instance,
            "test-worker-0");

        return (driver, dlqStore, worker, job);
    }

    private static QueueJobEntry MakeEntry(int maxAttempts = 3) => new()
    {
        QueueName = "test",
        JobType = typeof(CapturingJob).AssemblyQualifiedName!,
        Payload = JsonSerializer.Serialize(new TestPayload { Value = "hello" }),
        MaxAttempts = maxAttempts
    };

    [Fact]
    public async Task ProcessJob_CallsExecuteAsyncWithCorrectPayloadAndContext()
    {
        var (driver, _, worker, job) = BuildSystem();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = worker.StartAsync(cts.Token);

        await job.WaitForExecutionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        job.WasCalled.Should().BeTrue();
        job.ReceivedPayload!.Value.Should().Be("hello");
        job.ReceivedContext!.QueueName.Should().Be("test");
        job.ReceivedContext.JobId.Should().Be(entry.Id);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ProcessJob_OnException_BelowMaxAttempts_CallsFailAsync()
    {
        var (driver, _, worker, job) = BuildSystem(maxAttempts: 3, initialDelayMs: 60_000);
        job.ThrowOnExecute = new InvalidOperationException("boom");
        var entry = MakeEntry(maxAttempts: 3);

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = worker.StartAsync(cts.Token);

        await job.WaitForExecutionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddHours(1));
        stuck.Should().BeEmpty();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ProcessJob_OnException_AtMaxAttempts_WithDlq_MoveToDeadLetter()
    {
        var (driver, dlqStore, worker, job) = BuildSystem(maxAttempts: 1, dlqEnabled: true);
        job.ThrowOnExecute = new InvalidOperationException("fatal");
        var entry = MakeEntry(maxAttempts: 1);

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = worker.StartAsync(cts.Token);

        await job.WaitForExecutionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        dlqStore.Records.Should().ContainKey(entry.Id);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleWorkersAndMemoryDriver_LogsWarning()
    {
        var logger = new CapturingLogger<QueueWorkerService>();
        var dlqStore = new InMemoryDlqStore();
        var driver = new MemoryQueueDriver(50, dlqStore);
        var config = new QueueConfiguration
        {
            QueueName = "test",
            Concurrency = 1,
            MaxAttempts = 1,
            RetryPolicy = new RetryPolicy()
        };
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var worker = new QueueWorkerService(
            driver, config,
            sp.GetRequiredService<IServiceScopeFactory>(),
            logger,
            "w0",
            totalWorkerCount: 2);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        logger.Logs.Should().Contain(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("in-memory driver"));
    }

    [Fact]
    public async Task ProcessJob_UnresolvableJobType_WorkerContinuesRunning()
    {
        var (driver, _, worker, job) = BuildSystem(maxAttempts: 1);
        var badEntry = new QueueJobEntry
        {
            QueueName = "test",
            JobType = "NonExistent.Job, NonExistentAssembly",
            Payload = "{}",
            MaxAttempts = 1
        };

        var goodEntry = MakeEntry();

        await driver.EnqueueAsync(badEntry);
        await driver.EnqueueAsync(goodEntry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = worker.StartAsync(cts.Token);

        await job.WaitForExecutionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        job.WasCalled.Should().BeTrue();
        await cts.CancelAsync();
    }
}
