namespace DotInfraKit.Queue;

public sealed record QueueStats(
    string Name,
    long Pending,
    long Processing,
    long Delayed,
    long DeadLetter);
