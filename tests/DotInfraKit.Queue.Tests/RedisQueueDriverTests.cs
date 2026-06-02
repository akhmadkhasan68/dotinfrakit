using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Redis.Internal;
using FluentAssertions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace DotInfraKit.Queue.Tests;

public sealed class RedisQueueDriverTests : IAsyncLifetime
{
    private RedisContainer? _redis;
    private IConnectionMultiplexer? _mux;

    public async Task InitializeAsync()
    {
        if (!DockerAvailableFactAttribute.IsDockerAvailable()) return;
        _redis = new RedisBuilder().Build();
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        _mux?.Dispose();
        if (_redis is not null) await _redis.DisposeAsync();
    }

    private RedisQueueDriver CreateDriver(string prefix = "test:")
        => new(_mux!.GetDatabase(), prefix, "default");

    private static QueueJobEntry MakeEntry(DateTime? nextRunAt = null) => new()
    {
        QueueName = "default",
        JobType = "MyJob",
        Payload = "{}",
        MaxAttempts = 3,
        NextRunAt = nextRunAt
    };

    [DockerAvailableFact]
    public async Task Enqueue_ThenDequeue_ReturnsSameEntry()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dequeued = await driver.DequeueAsync("worker-1", cts.Token);

        dequeued.Should().NotBeNull();
        dequeued!.Id.Should().Be(entry.Id);
    }

    [DockerAvailableFact]
    public async Task Dequeue_SetsStatusAndLockFields()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dequeued = await driver.DequeueAsync("worker-1", cts.Token);

        dequeued!.Status.Should().Be("processing");
        dequeued.LockedAt.Should().NotBeNull();
        dequeued.LockedBy.Should().Be("worker-1");
    }

    [DockerAvailableFact]
    public async Task Complete_RemovesFromProcessing()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.CompleteAsync(entry.Id);

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddMinutes(10));
        stuck.Should().BeEmpty();
    }

    [DockerAvailableFact]
    public async Task Fail_PutsEntryIntoDelayed_NotProcessing()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.FailAsync(entry.Id, "error", DateTime.UtcNow.AddMinutes(1));

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddHours(1));
        stuck.Should().BeEmpty();
    }

    [DockerAvailableFact]
    public async Task DelayedJob_NotReturnedBeforeNextRunAt()
    {
        var driver = CreateDriver();
        var entry = MakeEntry(nextRunAt: DateTime.UtcNow.AddHours(1));

        await driver.EnqueueAsync(entry);

        var ready = await driver.GetReadyDelayedJobsAsync();
        ready.Should().BeEmpty();
    }

    [DockerAvailableFact]
    public async Task DelayedJob_PromotedAfterNextRunAt()
    {
        var driver = CreateDriver();
        var entry = MakeEntry(nextRunAt: DateTime.UtcNow.AddMilliseconds(50));

        await driver.EnqueueAsync(entry);

        var notReady = await driver.GetReadyDelayedJobsAsync();
        notReady.Should().BeEmpty();

        await Task.Delay(150);

        var ready = await driver.GetReadyDelayedJobsAsync();
        ready.Should().HaveCount(1);

        await driver.PromoteDelayedJobAsync(entry.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dequeued = await driver.DequeueAsync("worker-1", cts.Token);
        dequeued.Should().NotBeNull();
        dequeued!.Id.Should().Be(entry.Id);
    }

    [DockerAvailableFact]
    public async Task Fail_IncrementsAttemptsInRedis()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.FailAsync(entry.Id, "transient error", DateTime.UtcNow.AddMinutes(1));

        var json = await _mux!.GetDatabase().StringGetAsync($"test:job:{entry.Id:N}");
        var stored = System.Text.Json.JsonSerializer.Deserialize<DotInfraKit.Queue.Internal.QueueJobEntry>((string)json!)!;
        stored.Attempts.Should().Be(1);
    }

    [DockerAvailableFact]
    public async Task MoveToDeadLetter_StoresInDlqHash()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.MoveToDeadLetterAsync(entry.Id, "fatal error");

        var dlqValue = await _mux!.GetDatabase().HashGetAsync("test:dlq:default", entry.Id.ToString("N"));
        dlqValue.IsNull.Should().BeFalse();
        ((string)dlqValue!).Should().Contain("fatal error");
    }

    [DockerAvailableFact]
    public async Task GetStuckJobs_ReturnsJobsLockedBeforeThreshold()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddMinutes(5));
        stuck.Should().HaveCount(1);
        stuck[0].Id.Should().Be(entry.Id);
    }

    [DockerAvailableFact]
    public async Task RequeueStuckJob_IncrementsAttemptsAndClearsLock()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts1.Token);
        await driver.RequeueStuckJobAsync(entry.Id);

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var requeued = await driver.DequeueAsync("worker-2", cts2.Token);
        requeued.Should().NotBeNull();
        requeued!.Attempts.Should().Be(1);
        requeued.LockedBy.Should().Be("worker-2");
    }
}
