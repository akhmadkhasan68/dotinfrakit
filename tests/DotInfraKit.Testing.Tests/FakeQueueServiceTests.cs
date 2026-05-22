using DotInfraKit.Queue;
using DotInfraKit.Testing;
using FluentAssertions;

namespace DotInfraKit.Testing.Tests;

public sealed class FakeQueueServiceTests
{
    private sealed record TestPayload(string Name);

    private sealed class TestJob : IQueueJob<TestPayload>
    {
        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class OtherJob : IQueueJob<TestPayload>
    {
        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task EnqueueAsync_RecordsJob_AssertEnqueuedPasses()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        var act = () => fake.AssertEnqueued<TestJob>();
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertEnqueued_ThrowsWhenJobNotEnqueued()
    {
        var fake = new FakeQueueService();

        var act = () => fake.AssertEnqueued<TestJob>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TestJob*");
    }

    [Fact]
    public async Task AssertEnqueued_WithPredicate_MatchesPayload()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        var act = () => fake.AssertEnqueued<TestJob, TestPayload>(p => p.Name == "Alice");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task AssertEnqueued_WithPredicate_ThrowsWhenNoMatch()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        var act = () => fake.AssertEnqueued<TestJob, TestPayload>(p => p.Name == "Bob");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AssertNotEnqueued_PassesWhenNothingEnqueued()
    {
        var fake = new FakeQueueService();

        var act = () => fake.AssertNotEnqueued<TestJob>();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task AssertNotEnqueued_ThrowsWhenJobEnqueued()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));

        var act = () => fake.AssertNotEnqueued<TestJob>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TestJob*");
    }

    [Fact]
    public async Task AssertEnqueuedCount_MatchesExactCount()
    {
        var fake = new FakeQueueService();
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Alice"));
        await fake.EnqueueAsync<TestJob, TestPayload>(new TestPayload("Bob"));

        var passAct = () => fake.AssertEnqueuedCount<TestJob>(2);
        passAct.Should().NotThrow();

        var failAct = () => fake.AssertEnqueuedCount<TestJob>(3);
        failAct.Should().Throw<InvalidOperationException>();
    }
}
