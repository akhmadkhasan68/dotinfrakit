namespace DotInfraKit.Queue;

public sealed class JobContext
{
    public Guid JobId { get; init; }
    public string QueueName { get; init; } = string.Empty;
    public int AttemptNumber { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime EnqueuedAt { get; init; }
}
