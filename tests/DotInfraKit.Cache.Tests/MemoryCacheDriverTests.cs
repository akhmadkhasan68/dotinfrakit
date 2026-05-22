using DotInfraKit.Cache;
using DotInfraKit.Cache.Internal.Drivers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace DotInfraKit.Cache.Tests;

public sealed class MemoryCacheDriverTests
{
    private static MemoryCacheDriver CreateDriver(TimeSpan? defaultExpiry = null)
        => new(new MemoryCache(new MemoryCacheOptions()), defaultExpiry);

    [Fact]
    public async Task GetOrSetAsync_CallsFactoryOnMiss()
    {
        var driver = CreateDriver();
        var callCount = 0;

        var result = await driver.GetOrSetAsync("key", () =>
        {
            callCount++;
            return Task.FromResult(42);
        });

        result.Should().Be(42);
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrSetAsync_SkipsFactoryOnHit()
    {
        var driver = CreateDriver();
        await driver.SetAsync("key", 42);

        var callCount = 0;
        var result = await driver.GetOrSetAsync("key", () =>
        {
            callCount++;
            return Task.FromResult(99);
        });

        result.Should().Be(42);
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task ForgetAsync_RemovesKey()
    {
        var driver = CreateDriver();
        await driver.SetAsync("key", "value");
        await driver.ForgetAsync("key");

        var result = await driver.GetAsync<string>("key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ForgetByPrefixAsync_RemovesMatchingKeysOnly()
    {
        var driver = CreateDriver();
        await driver.SetAsync("api:users", "u");
        await driver.SetAsync("api:posts", "p");
        await driver.SetAsync("other", "o");

        await driver.ForgetByPrefixAsync("api:");

        (await driver.GetAsync<string>("api:users")).Should().BeNull();
        (await driver.GetAsync<string>("api:posts")).Should().BeNull();
        (await driver.GetAsync<string>("other")).Should().Be("o");
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForCachedKey()
    {
        var driver = CreateDriver();
        await driver.SetAsync("key", 1);

        (await driver.ExistsAsync("key")).Should().BeTrue();
        (await driver.ExistsAsync("missing")).Should().BeFalse();
    }

    [Fact]
    public async Task DefaultExpiry_AppliedWhenNoExpiryPassed()
    {
        var driver = CreateDriver(defaultExpiry: TimeSpan.FromMilliseconds(100));
        await driver.SetAsync("key", "value");

        (await driver.GetAsync<string>("key")).Should().Be("value");

        await Task.Delay(200);

        (await driver.GetAsync<string>("key")).Should().BeNull();
    }
}
