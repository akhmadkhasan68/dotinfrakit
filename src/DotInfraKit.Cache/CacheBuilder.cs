using DotInfraKit.Cache.Internal.Drivers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DotInfraKit.Cache;

public sealed class CacheBuilder
{
    internal bool _isMemory;
    internal Func<IServiceProvider, ICacheService>? DriverFactory;
    internal TimeSpan? DefaultExpiry;
    internal string? AutoInvalidationPrefix;

    public CacheBuilder UseMemory()
    {
        _isMemory = true;
        DriverFactory = sp => new MemoryCacheDriver(sp.GetRequiredService<IMemoryCache>(), DefaultExpiry);
        return this;
    }

    public CacheBuilder UseRedis(Action<CacheRedisOptions> configure)
    {
        var opts = new CacheRedisOptions();
        configure(opts);
        IConnectionMultiplexer? mux = null;

        DriverFactory = _ =>
        {
            if (mux is null)
            {
                var config = new ConfigurationOptions { DefaultDatabase = opts.Database };
                config.EndPoints.Add(opts.Endpoint);
                if (opts.Password is not null)
                    config.Password = opts.Password;
                mux = ConnectionMultiplexer.Connect(config);
            }
            return new RedisCacheDriver(mux, opts.KeyPrefix, DefaultExpiry);
        };
        return this;
    }

    public CacheBuilder UseRedisSentinel(Action<CacheRedisSentinelOptions> configure)
    {
        var opts = new CacheRedisSentinelOptions();
        configure(opts);
        IConnectionMultiplexer? masterMux = null;

        DriverFactory = _ =>
        {
            if (masterMux is null)
            {
                var sentinelConfig = new ConfigurationOptions { ServiceName = opts.ServiceName };
                foreach (var ep in opts.Endpoints)
                    sentinelConfig.EndPoints.Add(ep);

                var sentinel = ConnectionMultiplexer.Connect(sentinelConfig);

                var masterConfig = new ConfigurationOptions { DefaultDatabase = opts.Database };
                if (opts.Password is not null)
                    masterConfig.Password = opts.Password;

                masterMux = sentinel.GetSentinelMasterConnection(masterConfig);
            }
            return new RedisCacheDriver(masterMux, opts.KeyPrefix, DefaultExpiry);
        };
        return this;
    }

    public CacheBuilder UseRedisCluster(Action<CacheRedisClusterOptions> configure)
    {
        var opts = new CacheRedisClusterOptions();
        configure(opts);
        IConnectionMultiplexer? mux = null;

        DriverFactory = _ =>
        {
            if (mux is null)
            {
                var config = new ConfigurationOptions();
                foreach (var ep in opts.Endpoints)
                    config.EndPoints.Add(ep);
                if (opts.Password is not null)
                    config.Password = opts.Password;
                mux = ConnectionMultiplexer.Connect(config);
            }
            return new RedisClusterCacheDriver(mux, opts.KeyPrefix, DefaultExpiry);
        };
        return this;
    }

    public CacheBuilder WithDefaultExpiry(TimeSpan expiry)
    {
        DefaultExpiry = expiry;
        return this;
    }

    public CacheBuilder EnableAutoInvalidation(string prefix)
    {
        AutoInvalidationPrefix = prefix;
        return this;
    }
}
