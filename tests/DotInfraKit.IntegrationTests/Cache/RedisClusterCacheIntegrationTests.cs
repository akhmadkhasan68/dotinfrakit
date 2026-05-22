using DotInfraKit.Cache;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit.IntegrationTests;

[Trait("Category", "RedisCluster")]
public sealed class RedisClusterCacheIntegrationTests
{
    private ICacheService BuildCache()
    {
        var endpoints = RedisClusterSkipAttribute.GetEndpoints();
        var services = new ServiceCollection();
        services.AddAppCache(c =>
            c.UseRedisCluster(r =>
            {
                r.Endpoints = endpoints;
                r.KeyPrefix = $"inttest:{Guid.NewGuid():N}:";
            })
            .WithDefaultExpiry(TimeSpan.FromMinutes(5)));

        return services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    [RedisClusterSkip]
    public async Task ForgetByPrefixAsync_EvictsKeysFromAllClusterNodes()
    {
        var cache = BuildCache();

        // Keys hashing to different nodes (different hash slots)
        await cache.SetAsync("users:100", "Alice");
        await cache.SetAsync("users:200", "Bob");
        await cache.SetAsync("users:300", "Charlie");
        await cache.SetAsync("orders:1", "Order-A"); // different prefix — should survive

        await cache.ForgetByPrefixAsync("users:");

        (await cache.GetAsync<string>("users:100")).Should().BeNull();
        (await cache.GetAsync<string>("users:200")).Should().BeNull();
        (await cache.GetAsync<string>("users:300")).Should().BeNull();
        (await cache.GetAsync<string>("orders:1")).Should().Be("Order-A");
    }

    [RedisClusterSkip]
    public async Task SetAndGet_WorksAcrossClusterNodes()
    {
        var cache = BuildCache();

        // Set multiple keys that hash to different nodes
        await cache.SetAsync("key:alpha", "value-alpha");
        await cache.SetAsync("key:beta", "value-beta");
        await cache.SetAsync("key:gamma", "value-gamma");

        (await cache.GetAsync<string>("key:alpha")).Should().Be("value-alpha");
        (await cache.GetAsync<string>("key:beta")).Should().Be("value-beta");
        (await cache.GetAsync<string>("key:gamma")).Should().Be("value-gamma");
    }

    [RedisClusterSkip]
    public async Task ExistsAsync_ReturnsTrueAfterSet_FalseAfterForget()
    {
        var cache = BuildCache();

        await cache.SetAsync("check:1", "present");
        (await cache.ExistsAsync("check:1")).Should().BeTrue();

        await cache.ForgetAsync("check:1");
        (await cache.ExistsAsync("check:1")).Should().BeFalse();
    }
}
