using DotInfraKit.Queue.Database;
using DotInfraKit.Queue.Database.Internal;
using DotInfraKit.Queue.Internal;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotInfraKit.Queue.Tests;

public sealed class DatabaseQueueDriverTests : IAsyncLifetime
{
    private SqliteConnection? _connection;
    private TestQueueDbContextFactory? _factory;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TestQueueDbContextFactory(_connection);

        await using var ctx = _factory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private DatabaseQueueDriver<TestQueueDbContext> CreateDriver(string queueName = "default")
        => new(_factory!, queueName);

    private static QueueJobEntry MakeEntry(DateTime? nextRunAt = null) => new()
    {
        QueueName = "default",
        JobType = "MyJob",
        Payload = "{}",
        MaxAttempts = 3,
        NextRunAt = nextRunAt,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Complete_SetsStatusCompleted()
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

    [Fact]
    public async Task Fail_SetsRetryState()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        var retryAt = DateTime.UtcNow.AddMinutes(5);
        await driver.FailAsync(entry.Id, "transient error", retryAt);

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddHours(1));
        stuck.Should().BeEmpty();

        var delayed = await driver.GetReadyDelayedJobsAsync();
        delayed.Should().BeEmpty();
    }

    [Fact]
    public async Task Fail_IncrementsAttemptsInDatabase()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.FailAsync(entry.Id, "transient error", DateTime.UtcNow.AddMinutes(1));

        await using var ctx = _factory!.CreateDbContext();
        var record = await ctx.Set<QueueJobRecord>().FindAsync(entry.Id);
        record!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task MoveToDeadLetter_SetsStatusDead()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();

        await driver.EnqueueAsync(entry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await driver.DequeueAsync("worker-1", cts.Token);
        await driver.MoveToDeadLetterAsync(entry.Id, "fatal error");

        var stuck = await driver.GetStuckJobsAsync(DateTime.UtcNow.AddHours(1));
        stuck.Should().BeEmpty();

        await using var ctx = _factory!.CreateDbContext();
        var record = await ctx.Set<QueueJobRecord>().FindAsync(entry.Id);
        record!.Status.Should().Be("dead");
        record.ErrorMessage.Should().Be("fatal error");
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Dequeue_OptimisticLock_OnlyOneWorkerWins()
    {
        var driver = CreateDriver();
        var entry = MakeEntry();
        await driver.EnqueueAsync(entry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task1 = driver.DequeueAsync("worker-1", cts.Token);
        var task2 = driver.DequeueAsync("worker-2", cts.Token);

        var results = await Task.WhenAll(task1, task2);

        var claimed = results.Where(r => r is not null).ToList();
        claimed.Should().HaveCount(1);
        claimed[0]!.Id.Should().Be(entry.Id);
    }

    private sealed class TestQueueDbContext : DbContext
    {
        public TestQueueDbContext(DbContextOptions<TestQueueDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddDotInfraKitQueue();
        }
    }

    private sealed class TestQueueDbContextFactory : IDbContextFactory<TestQueueDbContext>
    {
        private readonly SqliteConnection _connection;

        public TestQueueDbContextFactory(SqliteConnection connection) => _connection = connection;

        public TestQueueDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<TestQueueDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new TestQueueDbContext(options);
        }

        public Task<TestQueueDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
