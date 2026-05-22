using DotInfraKit.Cache;

namespace DotInfraKit.Testing;

public sealed class FakeCacheService : ICacheService
{
    private readonly Dictionary<string, object?> _store = [];
    private readonly Dictionary<string, int> _getCalls = [];
    private readonly Dictionary<string, int> _factoryCalls = [];

    public Task<T?> GetAsync<T>(string key)
    {
        Increment(_getCalls, key);
        _store.TryGetValue(key, out var value);
        return Task.FromResult((T?)value);
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        Increment(_getCalls, key);
        if (_store.TryGetValue(key, out var cached))
            return (T)cached!;
        Increment(_factoryCalls, key);
        var value = await factory();
        _store[key] = value;
        return value;
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string key)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task ForgetByPrefixAsync(string prefix)
    {
        foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _store.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key)
        => Task.FromResult(_store.ContainsKey(key));

    public int GetCallCount(string key)
        => _getCalls.TryGetValue(key, out var count) ? count : 0;

    public int FactoryCallCount(string key)
        => _factoryCalls.TryGetValue(key, out var count) ? count : 0;

    private static void Increment(Dictionary<string, int> dict, string key)
        => dict[key] = dict.TryGetValue(key, out var c) ? c + 1 : 1;
}
