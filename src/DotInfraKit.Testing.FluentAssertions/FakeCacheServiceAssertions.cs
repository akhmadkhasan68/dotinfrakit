using DotInfraKit.Testing;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Primitives;

namespace DotInfraKit.Testing.FluentAssertions;

public static class FakeCacheServiceShouldExtensions
{
    public static FakeCacheServiceAssertions Should(this FakeCacheService instance)
        => new(instance);
}

public sealed class FakeCacheServiceAssertions
    : ReferenceTypeAssertions<FakeCacheService, FakeCacheServiceAssertions>
{
    public FakeCacheServiceAssertions(FakeCacheService subject) : base(subject) { }

    protected override string Identifier => "FakeCacheService";

    public AndConstraint<FakeCacheServiceAssertions> HaveKey(
        string key, string because = "", params object[] becauseArgs)
    {
        var exists = Subject.ExistsAsync(key).GetAwaiter().GetResult();
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(exists)
            .FailWith(
                "Expected {context:FakeCacheService} to have key '{0}'{reason}, but it was not found.", key);
        return new AndConstraint<FakeCacheServiceAssertions>(this);
    }

    public AndConstraint<FakeCacheServiceAssertions> NotHaveKey(
        string key, string because = "", params object[] becauseArgs)
    {
        var exists = Subject.ExistsAsync(key).GetAwaiter().GetResult();
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(!exists)
            .FailWith(
                "Expected {context:FakeCacheService} not to have key '{0}'{reason}, but it was found.", key);
        return new AndConstraint<FakeCacheServiceAssertions>(this);
    }

    public AndConstraint<FakeCacheServiceAssertions> HaveGetCallCount(
        string key, int expected, string because = "", params object[] becauseArgs)
    {
        var actual = Subject.GetCallCount(key);
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(actual == expected)
            .FailWith(
                "Expected {context:FakeCacheService} to have {0} get call(s) for key '{1}'{reason}, but found {2}.",
                expected, key, actual);
        return new AndConstraint<FakeCacheServiceAssertions>(this);
    }

    public AndConstraint<FakeCacheServiceAssertions> HaveFactoryCallCount(
        string key, int expected, string because = "", params object[] becauseArgs)
    {
        var actual = Subject.FactoryCallCount(key);
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(actual == expected)
            .FailWith(
                "Expected {context:FakeCacheService} to have {0} factory call(s) for key '{1}'{reason}, but found {2}.",
                expected, key, actual);
        return new AndConstraint<FakeCacheServiceAssertions>(this);
    }
}
