namespace DotInfraKit.Queue.Database.Internal;

internal sealed class QueueJobRecord
{
    public Guid Id { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
