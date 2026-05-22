namespace DotInfraKit.Queue.Internal.Configuration;

internal sealed class QueueConfiguration
{
    public string QueueName { get; init; } = "default";
    public int WorkerCount { get; init; } = 1;
    public int Concurrency { get; init; } = 5;
    public int MaxAttempts { get; init; } = 3;
    public RetryPolicy RetryPolicy { get; init; } = new();
    public bool DlqEnabled { get; init; }
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DelayedJobPollingInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int ChannelCapacity { get; init; } = 100;
}
