using DotInfraKit.Queue.Internal.Configuration;

namespace DotInfraKit.Queue.Internal;

internal sealed class QueueRegistry
{
    private readonly IReadOnlyList<(string Name, QueueConfiguration Config)> _entries;

    public QueueRegistry(IEnumerable<(string Name, QueueConfiguration Config)> entries)
        => _entries = entries.ToList().AsReadOnly();

    public IReadOnlyList<string> Names => _entries.Select(e => e.Name).ToList();

    public QueueConfiguration? GetConfig(string name)
        => _entries.FirstOrDefault(e => e.Name == name).Config;

    public IReadOnlyList<QueueInfo> ToQueueInfoList()
        => _entries.Select(e => ToInfo(e.Config)).ToList();

    private static QueueInfo ToInfo(QueueConfiguration c) => new(
        c.QueueName,
        c.WorkerCount,
        c.Concurrency,
        c.MaxAttempts,
        c.RetryPolicy.BackoffType,
        c.DlqEnabled,
        c.LockTimeout,
        c.DelayedJobPollingInterval);
}
