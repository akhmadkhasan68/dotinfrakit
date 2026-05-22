namespace DotInfraKit.Queue.Internal.Configuration;

public sealed class QueueOptions
{
    private readonly List<QueueBuilder> _queues = [];

    public void UseDefaultQueue(Action<QueueBuilder> configure)
    {
        var builder = new QueueBuilder("default");
        configure(builder);
        _queues.Add(builder);
    }

    public void AddQueue(string name, Action<QueueBuilder> configure)
    {
        var builder = new QueueBuilder(name);
        configure(builder);
        _queues.Add(builder);
    }

    internal IReadOnlyList<QueueBuilder> Queues => _queues;
}
