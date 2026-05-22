using StackExchange.Redis;

namespace DotInfraKit.Cache.Internal.Drivers;

internal sealed class RedisClusterCacheDriver : ICacheService
{
    private readonly RedisCacheDriver _inner;
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private readonly string _keyPrefix;

    internal RedisClusterCacheDriver(IConnectionMultiplexer mux, string keyPrefix, TimeSpan? defaultExpiry)
    {
        _mux = mux;
        _db = mux.GetDatabase();
        _keyPrefix = keyPrefix;
        _inner = new RedisCacheDriver(mux, keyPrefix, defaultExpiry);
    }

    public Task<T?> GetAsync<T>(string key) => _inner.GetAsync<T>(key);
    public Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null) => _inner.GetOrSetAsync(key, factory, expiry);
    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null) => _inner.SetAsync(key, value, expiry);
    public Task ForgetAsync(string key) => _inner.ForgetAsync(key);
    public Task<bool> ExistsAsync(string key) => _inner.ExistsAsync(key);

    public async Task ForgetByPrefixAsync(string prefix)
    {
        var masters = _mux.GetEndPoints()
            .Select(ep => _mux.GetServer(ep))
            .Where(s => s.IsConnected && !s.IsReplica)
            .ToList();

        await Parallel.ForEachAsync(masters, async (server, ct) =>
        {
            var keys = server.Keys(pattern: $"{_keyPrefix}{prefix}*").ToArray();
            if (keys.Length > 0)
                await _db.KeyDeleteAsync(keys);
        });
    }
}
