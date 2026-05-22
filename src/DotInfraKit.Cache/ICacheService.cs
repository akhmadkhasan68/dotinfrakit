namespace DotInfraKit.Cache;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key);
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);
    Task ForgetAsync(string key);
    Task ForgetByPrefixAsync(string prefix);
    Task<bool> ExistsAsync(string key);
}
