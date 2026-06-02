namespace DotInfraKit.Queue;

public sealed class DlqJobRecord
{
    public Guid Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime DeadAt { get; set; }
}
