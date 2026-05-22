namespace DotInfraKit.Queue.Internal;

internal sealed class QueueJobEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string QueueName { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public int Priority { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
