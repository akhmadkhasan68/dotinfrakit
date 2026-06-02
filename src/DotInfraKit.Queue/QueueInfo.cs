namespace DotInfraKit.Queue;

public sealed record QueueInfo(
    string Name,
    int WorkerCount,
    int Concurrency,
    int MaxAttempts,
    BackoffType BackoffType,
    bool DlqEnabled,
    TimeSpan LockTimeout,
    TimeSpan DelayedJobPollingInterval);
