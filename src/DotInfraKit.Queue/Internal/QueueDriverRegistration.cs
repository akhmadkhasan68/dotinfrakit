using DotInfraKit.Queue.Internal.Drivers;

namespace DotInfraKit.Queue.Internal;

internal sealed class QueueDriverRegistration
{
    public Func<IQueueDriver>? CreateDriver { get; init; }
    public Func<IServiceProvider, IQueueDriver>? CreateDriverFromProvider { get; init; }
    public Func<IQueueDriver, IDlqService>? CreateDlqService { get; init; }
    public Func<IServiceProvider, IQueueDriver, IDlqService>? CreateDlqServiceFromProvider { get; init; }
}
