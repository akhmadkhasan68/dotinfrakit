using System.Collections.Concurrent;

namespace DotInfraKit.Queue.Internal;

internal sealed class InMemoryDlqStore
{
    public ConcurrentDictionary<Guid, DlqJobRecord> Records { get; } = new();
}
