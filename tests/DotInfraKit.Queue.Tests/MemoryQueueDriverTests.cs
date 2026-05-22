using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Drivers;
using FluentAssertions;

namespace DotInfraKit.Queue.Tests;

public class MemoryQueueDriverTests
{
    private static MemoryQueueDriver CreateDriver(int capacity = 50)
        => new(capacity, new InMemoryDlqStore());

    private static QueueJobEntry MakeEntry(DateTime? nextRunAt = null) => new()
    {
        QueueName = "test",
        JobType = "MyJob",
        Payload = "{}",
        MaxAttempts = 3,
        NextRunAt = nextRunAt
    };

    [Fact]
    public async Task Enqueue_ThenDequeue_ReturnsSameEntry()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        var dequeued = await driver.DequeueAsync("worker-1");

        dequeued.Should().NotBeNull();
        dequeued!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Dequeue_SetsStatusAndLockFields()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        var dequeued = await driver.DequeueAsync("worker-1");

        dequeued!.Status.Should().Be("processing");
        dequeued.LockedAt.Should().NotBeNull();
        dequeued.LockedBy.Should().Be("worker-1");
    }

    [Fact]
    public async Task Complete_RemovesFromProcessing()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        await driver.DequeueAsync("worker-1");
        await driver.CompleteAsync(entry.Id);

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddMinutes(10));
        stuck.Should().BeEmpty();
    }

    [Fact]
    public async Task Fail_PutsEntryIntoDelayed_NotProcessing()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        await driver.DequeueAsync("worker-1");
        await driver.FailAsync(entry.Id, "error", DateTime.UtcNow.AddMinutes(1));

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddHours(1));
        stuck.Should().BeEmpty();
    }

    [Fact]
    public async Task DelayedJob_NotReturnedBeforeNextRunAt()
    {
        var driver = CreateDriver();
        var entry = MakeEntry(nextRunAt: DateTime.UtcNow.AddHours(1));

        await driver.EnqueueAsync(entry);

        var ready = await driver.GetReadyDelayedJobsAsync();
        ready.Should().BeEmpty();
    }

    [Fact]
    public async Task DelayedJob_PromotedAfterNextRunAt()
    {
        var driver = CreateDriver();
        var entry = MakeEntry(nextRunAt: DateTime.UtcNow.AddMilliseconds(50));

        await driver.EnqueueAsync(entry);

        var notReadyYet = await driver.GetReadyDelayedJobsAsync();
        notReadyYet.Should().BeEmpty();

        await Task.Delay(120);

        var ready = await driver.GetReadyDelayedJobsAsync();
        ready.Should().HaveCount(1);

        await driver.PromoteDelayedJobAsync(entry.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var dequeued = await driver.DequeueAsync("worker-1", cts.Token);
        dequeued.Should().NotBeNull();
        dequeued!.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task MoveToDeadLetter_AddsRecordToDlqStore()
    {
        var dlqStore = new InMemoryDlqStore();
        var driver = new MemoryQueueDriver(50, dlqStore);
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        await driver.DequeueAsync("worker-1");
        await driver.MoveToDeadLetterAsync(entry.Id, "fatal error");

        dlqStore.Records.Should().ContainKey(entry.Id);
        dlqStore.Records[entry.Id].ErrorMessage.Should().Be("fatal error");
    }

    [Fact]
    public async Task GetStuckJobs_ReturnsJobsLockedBeforeThreshold()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        await driver.DequeueAsync("worker-1");

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddMinutes(5));
        stuck.Should().HaveCount(1);
        stuck[0].Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task RequeueStuckJob_IncrementsAttemptsAndClearsLock()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        await driver.DequeueAsync("worker-1");
        await driver.RequeueStuckJobAsync(entry.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var requeued = await driver.DequeueAsync("worker-2", cts.Token);
        requeued.Should().NotBeNull();
        requeued!.Attempts.Should().Be(1);
        requeued.LockedBy.Should().Be("worker-2");
    }
}
