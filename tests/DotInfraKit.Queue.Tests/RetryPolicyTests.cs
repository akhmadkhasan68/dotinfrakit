using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using FluentAssertions;

namespace DotInfraKit.Queue.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(BackoffType.Exponential, 2000, 1, 2000)]
    [InlineData(BackoffType.Exponential, 2000, 2, 4000)]
    [InlineData(BackoffType.Exponential, 2000, 3, 8000)]
    [InlineData(BackoffType.Fixed, 5000, 1, 5000)]
    [InlineData(BackoffType.Fixed, 5000, 3, 5000)]
    [InlineData(BackoffType.Linear, 1000, 3, 3000)]
    public void CalculateDelay_ReturnsExpectedMs(BackoffType type, int initialMs, int attempt, int expectedMs)
    {
        var policy = new RetryPolicy { BackoffType = type, InitialDelayMs = initialMs };

        var delay = policy.CalculateDelay(attempt);

        delay.TotalMilliseconds.Should().Be(expectedMs);
    }

    [Fact]
    public void CalculateDelay_InvalidBackoffType_ThrowsArgumentOutOfRangeException()
    {
        var policy = new RetryPolicy { BackoffType = (BackoffType)99 };

        var act = () => policy.CalculateDelay(1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
