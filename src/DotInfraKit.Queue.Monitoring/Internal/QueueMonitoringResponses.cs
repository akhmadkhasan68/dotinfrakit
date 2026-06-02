using DotInfraKit.Queue;

namespace DotInfraKit.Queue.Monitoring.Internal;

internal sealed record QueueListResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<QueueJobDetail> Data);

internal sealed record QueueStatsResponse(
    DateTime Timestamp,
    IReadOnlyList<QueueStats> Queues);
