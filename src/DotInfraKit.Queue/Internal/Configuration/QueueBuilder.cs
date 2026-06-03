using DotInfraKit.Queue.Internal.Drivers;
using DotInfraKit.Queue.Internal.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit.Queue.Internal.Configuration;

public sealed class QueueBuilder
{
    internal string QueueName { get; }
    private int _workerCount = 1;
    private int _concurrency = 5;
    private int _maxAttempts = 3;
    private BackoffType _backoffType = BackoffType.Exponential;
    private int _initialDelayMs = 2000;
    private bool _dlqEnabled;
    private TimeSpan _lockTimeout = TimeSpan.FromMinutes(5);
    private TimeSpan _delayedJobPollingInterval = TimeSpan.FromSeconds(5);
    private TimeSpan _pollingInterval = TimeSpan.FromSeconds(3);
    private int _channelCapacity = 100;

    internal QueueDriverRegistration? _driverRegistration;
    internal List<Action<IServiceCollection>> _startupRegistrations = [];

    internal QueueBuilder(string queueName)
    {
        QueueName = queueName;
    }

    public QueueBuilder UseMemoryDriver(int capacity = 100)
    {
        _channelCapacity = capacity;
        var dlqStore = new InMemoryDlqStore();
        _driverRegistration = new QueueDriverRegistration
        {
            CreateDriver = () => new MemoryQueueDriver(capacity, dlqStore),
            CreateDlqService = driver => new InMemoryDlqService(dlqStore, driver)
        };
        return this;
    }

    public QueueBuilder Workers(int count = 1, int concurrency = 5)
    {
        _workerCount = count;
        _concurrency = concurrency;
        return this;
    }

    public QueueBuilder Retry(int maxAttempts, BackoffType backoffType, int initialDelayMs = 2000)
    {
        _maxAttempts = maxAttempts;
        _backoffType = backoffType;
        _initialDelayMs = initialDelayMs;
        return this;
    }

    public QueueBuilder EnableDeadLetterQueue()
    {
        _dlqEnabled = true;
        return this;
    }

    public QueueBuilder LockTimeout(TimeSpan timeout)
    {
        _lockTimeout = timeout;
        return this;
    }

    public QueueBuilder DelayedJobPollingInterval(TimeSpan interval)
    {
        _delayedJobPollingInterval = interval;
        return this;
    }

    public QueueBuilder PollingInterval(TimeSpan interval)
    {
        _pollingInterval = interval;
        return this;
    }

    internal QueueConfiguration Build() => new()
    {
        QueueName = QueueName,
        WorkerCount = _workerCount,
        Concurrency = _concurrency,
        MaxAttempts = _maxAttempts,
        RetryPolicy = new RetryPolicy { BackoffType = _backoffType, InitialDelayMs = _initialDelayMs },
        DlqEnabled = _dlqEnabled,
        LockTimeout = _lockTimeout,
        DelayedJobPollingInterval = _delayedJobPollingInterval,
        PollingInterval = _pollingInterval,
        ChannelCapacity = _channelCapacity
    };
}
