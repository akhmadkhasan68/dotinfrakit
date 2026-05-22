using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace DotInfraKit.Cache.Internal.Drivers;

internal sealed class MemoryCacheDriver : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan? _defaultExpiry;
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    internal MemoryCacheDriver(IMemoryCache cache, TimeSpan? defaultExpiry)
    {
        _cache = cache;
        _defaultExpiry = defaultExpiry;
    }

    public Task<T?> GetAsync<T>(string key)
    {
        _cache.TryGetValue(key, out T? value);
        return Task.FromResult(value);
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        if (_cache.TryGetValue(key, out T? cached))
            return cached!;
        var value = await factory();
        await SetAsync(key, value, expiry);
        return value;
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        var effective = expiry ?? _defaultExpiry;
        if (effective.HasValue)
            _cache.Set(key, value, effective.Value);
        else
            _cache.Set(key, value);
        _keys.TryAdd(key, 0);
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string key)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task ForgetByPrefixAsync(string prefix)
    {
        var matching = _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (var key in matching)
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
        => Task.FromResult(_cache.TryGetValue(key, out _));
}
