using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using DotInfraKit.Queue.Internal.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DotInfraKit.Queue.Tests;

public sealed class QueueServiceTests
{
    private sealed class TestPayload { }

    private sealed class TestJob : IQueueJob<TestPayload>
    {
        public Task ExecuteAsync(TestPayload payload, JobContext context, CancellationToken ct)
            => Task.CompletedTask;
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

    [Fact]
    public async Task EnqueueAsync_WithPriorityGreaterThanZero_LogsWarning()
    {
        var logger = new CapturingLogger<QueueService>();
        var dlqStore = new InMemoryDlqStore();
        var driver = new MemoryQueueDriver(50, dlqStore);
        var config = new QueueConfiguration
        {
            QueueName = "default",
            MaxAttempts = 3,
            Concurrency = 1,
            RetryPolicy = new RetryPolicy()
        };

        var service = new QueueService(
            new Dictionary<string, (IQueueDriver, QueueConfiguration)>
            {
                ["default"] = (driver, config)
            },
            logger);

        await service.EnqueueAsync<TestJob, TestPayload>("default", new TestPayload(),
            new EnqueueOptions { Priority = 5 });

        logger.Logs.Should().Contain(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("Priority"));
    }
}
