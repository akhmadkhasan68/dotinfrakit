using System.Text.Json;
using StackExchange.Redis;

namespace DotInfraKit.Cache.Internal.Drivers;

internal sealed class RedisCacheDriver : ICacheService
{
    private readonly IConnectionMultiplexer _mux;
    private readonly IDatabase _db;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _defaultExpiry;

    internal RedisCacheDriver(IConnectionMultiplexer mux, string keyPrefix, TimeSpan? defaultExpiry)
    {
        _mux = mux;
        _db = mux.GetDatabase();
        _keyPrefix = keyPrefix;
        _defaultExpiry = defaultExpiry;
    }

    private string K(string key) => $"{_keyPrefix}{key}";

    public async Task<T?> GetAsync<T>(string key)
    {
        var json = await _db.StringGetAsync(K(key));
        return json.IsNull ? default : JsonSerializer.Deserialize<T>((string)json!);
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        var json = await _db.StringGetAsync(K(key));
        if (!json.IsNull)
            return JsonSerializer.Deserialize<T>((string)json!)!;
        var value = await factory();
        await SetAsync(key, value, expiry);
        return value;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        var json = JsonSerializer.Serialize(value);
        var effective = expiry ?? _defaultExpiry;
        await _db.StringSetAsync(K(key), json, effective);
    }

    public Task ForgetAsync(string key) => _db.KeyDeleteAsync(K(key));

    public async Task ForgetByPrefixAsync(string prefix)
    {
        var server = _mux.GetServer(_mux.GetEndPoints().First());
        var keys = server.Keys(pattern: $"{_keyPrefix}{prefix}*").ToArray();
        if (keys.Length > 0)
            await _db.KeyDeleteAsync(keys);
    }

    public Task<bool> ExistsAsync(string key) => _db.KeyExistsAsync(K(key));
}
