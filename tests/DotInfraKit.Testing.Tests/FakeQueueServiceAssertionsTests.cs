using DotInfraKit.Queue;
using DotInfraKit.Testing;
using DotInfraKit.Testing.FluentAssertions;
using FluentAssertions;

namespace DotInfraKit.Testing.Tests;

public sealed class FakeQueueServiceAssertionsTests
{
    private sealed record TestPayload(string Name);

    private sealed class TestJob : IQueueJob<TestPayload>
    {
        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task HaveEnqueued_PassesWhenJobEnqueued()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        Action act = () => fake.Should().HaveEnqueued<TestJob>();
        act.Should().NotThrow();
    }

    [Fact]
    public void HaveEnqueued_FailsWhenJobNotEnqueued()
    {
        var fake = new FakeQueueService();

        Action act = () => fake.Should().HaveEnqueued<TestJob>();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task HaveEnqueued_WithPayload_PassesWhenMatches()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        Action act = () => fake.Should().HaveEnqueued<TestJob>().WithPayload<TestPayload>(p => p.Name == "Alice");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task HaveEnqueued_WithPayload_FailsWhenNoMatch()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        Action act = () => fake.Should().HaveEnqueued<TestJob>().WithPayload<TestPayload>(p => p.Name == "Bob");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void NotHaveEnqueued_PassesWhenEmpty()
    {
        var fake = new FakeQueueService();

        Action act = () => fake.Should().NotHaveEnqueued<TestJob>();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task NotHaveEnqueued_FailsWhenEnqueued()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        Action act = () => fake.Should().NotHaveEnqueued<TestJob>();
        act.Should().Throw<Exception>();
    }
}
