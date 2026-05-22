using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using DotInfraKit.Queue.Redis.Internal;
using StackExchange.Redis;

namespace DotInfraKit.Queue.Redis;

public static class QueueBuilderRedisExtensions
{
    public static QueueBuilder UseRedis(this QueueBuilder builder, Action<RedisOptions> configure)
    {
        var opts = new RedisOptions();
        configure(opts);

        var config = new ConfigurationOptions { DefaultDatabase = opts.Database };
        config.EndPoints.Add(opts.Endpoint);
        if (opts.Password is not null) config.Password = opts.Password;

        return SetRegistration(builder, config, opts.KeyPrefix);
    }

    public static QueueBuilder UseRedisSentinel(this QueueBuilder builder, Action<RedisSentinelOptions> configure)
    {
        var opts = new RedisSentinelOptions();
        configure(opts);

        var sentinelConfig = new ConfigurationOptions
        {
            ServiceName = opts.ServiceName,
            CommandMap = CommandMap.Sentinel,
            DefaultDatabase = opts.Database
        };
        foreach (var ep in opts.Endpoints)
            sentinelConfig.EndPoints.Add(ep);
        if (opts.Password is not null) sentinelConfig.Password = opts.Password;

        var masterConfig = new ConfigurationOptions
        {
            ServiceName = opts.ServiceName,
            DefaultDatabase = opts.Database
        };
        if (opts.Password is not null) masterConfig.Password = opts.Password;

        var queueName = builder.QueueName;
        var keyPrefix = opts.KeyPrefix;

        IConnectionMultiplexer? mux = null;
        IDatabase GetDb()
        {
            if (mux is not null) return mux.GetDatabase();
            var sentinel = ConnectionMultiplexer.Connect(sentinelConfig);
            mux = sentinel.GetSentinelMasterConnection(masterConfig);
            return mux.GetDatabase();
        }

        builder._driverRegistration = new QueueDriverRegistration
        {
            CreateDriver = () => new RedisQueueDriver(GetDb(), keyPrefix, queueName),
            CreateDlqService = driver => new RedisDlqService(GetDb(), keyPrefix, queueName, driver)
        };
        return builder;
    }

    public static QueueBuilder UseRedisCluster(this QueueBuilder builder, Action<RedisClusterOptions> configure)
    {
        var opts = new RedisClusterOptions();
        configure(opts);

        var config = new ConfigurationOptions();
        foreach (var ep in opts.Endpoints)
            config.EndPoints.Add(ep);
        if (opts.Password is not null) config.Password = opts.Password;

        return SetRegistration(builder, config, opts.KeyPrefix);
    }

    private static QueueBuilder SetRegistration(QueueBuilder builder, ConfigurationOptions config, string keyPrefix)
    {
        var queueName = builder.QueueName;

        IConnectionMultiplexer? mux = null;
        IDatabase GetDb()
        {
            mux ??= ConnectionMultiplexer.Connect(config);
            return mux.GetDatabase();
        }

        builder._driverRegistration = new QueueDriverRegistration
        {
            CreateDriver = () => new RedisQueueDriver(GetDb(), keyPrefix, queueName),
            CreateDlqService = driver => new RedisDlqService(GetDb(), keyPrefix, queueName, driver)
        };
        return builder;
    }
}
