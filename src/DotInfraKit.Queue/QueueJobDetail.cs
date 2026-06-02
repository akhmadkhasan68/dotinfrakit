namespace DotInfraKit.Queue;

public sealed record QueueJobDetail(
    Guid Id,
    string QueueName,
    string JobType,
    string Payload,
    string Status,
    int Attempts,
    int MaxAttempts,
    int Priority,
    DateTime? NextRunAt,
    DateTime? LockedAt,
    string? LockedBy,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? CompletedAt);
