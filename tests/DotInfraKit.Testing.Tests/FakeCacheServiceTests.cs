using DotInfraKit.Testing;
using FluentAssertions;

namespace DotInfraKit.Testing.Tests;

public sealed class FakeCacheServiceTests
{
    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsValue()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", "hello");

        var result = await fake.GetAsync<string>("key");
        result.Should().Be("hello");
    }

    [Fact]
    public async Task ForgetAsync_RemovesKey()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", "hello");
        await fake.ForgetAsync("key");

        var result = await fake.GetAsync<string>("key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ForgetByPrefixAsync_RemovesMatchingKeysOnly()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("api:users", "u");
        await fake.SetAsync("api:posts", "p");
        await fake.SetAsync("other", "o");

        await fake.ForgetByPrefixAsync("api:");

        (await fake.GetAsync<string>("api:users")).Should().BeNull();
        (await fake.GetAsync<string>("api:posts")).Should().BeNull();
        (await fake.GetAsync<string>("other")).Should().Be("o");
    }

    [Fact]
    public async Task GetOrSetAsync_IncrementsCounts()
    {
        var fake = new FakeCacheService();

        await fake.GetOrSetAsync("key", () => Task.FromResult(42));
        await fake.GetOrSetAsync("key", () => Task.FromResult(99));
        await fake.GetOrSetAsync("key", () => Task.FromResult(99));

        fake.GetCallCount("key").Should().Be(3);
        fake.FactoryCallCount("key").Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_IncrementsGetCallCount()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", 1);

        await fake.GetAsync<int>("key");
        await fake.GetAsync<int>("key");

        fake.GetCallCount("key").Should().Be(2);
        fake.FactoryCallCount("key").Should().Be(0);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueOnlyForStoredKey()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", "value");

        (await fake.ExistsAsync("key")).Should().BeTrue();
        (await fake.ExistsAsync("missing")).Should().BeFalse();
    }
}
