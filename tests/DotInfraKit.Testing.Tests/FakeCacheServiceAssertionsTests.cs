using DotInfraKit.Testing;
using DotInfraKit.Testing.FluentAssertions;
using FluentAssertions;

namespace DotInfraKit.Testing.Tests;

public sealed class FakeCacheServiceAssertionsTests
{
    [Fact]
    public async Task HaveKey_PassesWhenKeyExists()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", "hello");

        Action act = () => fake.Should().HaveKey("key");
        act.Should().NotThrow();
    }

    [Fact]
    public void HaveKey_FailsWhenKeyAbsent()
    {
        var fake = new FakeCacheService();

        Action act = () => fake.Should().HaveKey("missing");
        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task NotHaveKey_PassesAfterForget()
    {
        var fake = new FakeCacheService();
        await fake.SetAsync("key", "hello");
        await fake.ForgetAsync("key");

        Action act = () => fake.Should().NotHaveKey("key");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task HaveGetCallCount_MatchesActualCount()
    {
        var fake = new FakeCacheService();
        await fake.GetAsync<string>("key");
        await fake.GetAsync<string>("key");

        Action act = () => fake.Should().HaveGetCallCount("key", 2);
        act.Should().NotThrow();

        Action failAct = () => fake.Should().HaveGetCallCount("key", 99);
        failAct.Should().Throw<Exception>();
    }

    [Fact]
    public async Task HaveFactoryCallCount_MatchesActualCount()
    {
        var fake = new FakeCacheService();
        await fake.GetOrSetAsync("key", () => Task.FromResult(1));
        await fake.GetOrSetAsync("key", () => Task.FromResult(2));
        await fake.GetOrSetAsync("key", () => Task.FromResult(3));

        Action act = () => fake.Should().HaveFactoryCallCount("key", 1);
        act.Should().NotThrow();
    }
}
