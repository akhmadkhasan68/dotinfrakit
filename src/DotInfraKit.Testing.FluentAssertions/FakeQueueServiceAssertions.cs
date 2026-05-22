using DotInfraKit.Testing;
using FluentAssertions;
using FluentAssertions.Execution;
using FluentAssertions.Primitives;

namespace DotInfraKit.Testing.FluentAssertions;

public static class FakeQueueServiceShouldExtensions
{
    public static FakeQueueServiceAssertions Should(this FakeQueueService instance)
        => new(instance);
}

public sealed class FakeQueueServiceAssertions
    : ReferenceTypeAssertions<FakeQueueService, FakeQueueServiceAssertions>
{
    public FakeQueueServiceAssertions(FakeQueueService subject) : base(subject) { }

    protected override string Identifier => "FakeQueueService";

    public HaveEnqueuedChain<TJob> HaveEnqueued<TJob>(string because = "", params object[] becauseArgs)
    {
        var found = TryAssert(() => Subject.AssertEnqueued<TJob>());
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(found)
            .FailWith(
                "Expected {context:FakeQueueService} to have enqueued at least one '{0}' job{reason}, but none were found.",
                typeof(TJob).Name);
        return new HaveEnqueuedChain<TJob>(this);
    }

    public AndConstraint<FakeQueueServiceAssertions> NotHaveEnqueued<TJob>(
        string because = "", params object[] becauseArgs)
    {
        var notFound = TryAssert(() => Subject.AssertNotEnqueued<TJob>());
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(notFound)
            .FailWith(
                "Expected {context:FakeQueueService} not to have enqueued any '{0}' jobs{reason}, but some were found.",
                typeof(TJob).Name);
        return new AndConstraint<FakeQueueServiceAssertions>(this);
    }

    public AndConstraint<FakeQueueServiceAssertions> HaveEnqueuedCount<TJob>(
        int expected, string because = "", params object[] becauseArgs)
    {
        var matched = TryAssert(() => Subject.AssertEnqueuedCount<TJob>(expected));
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(matched)
            .FailWith(
                "Expected {context:FakeQueueService} to have enqueued exactly {0} '{1}' job(s){reason}.",
                expected, typeof(TJob).Name);
        return new AndConstraint<FakeQueueServiceAssertions>(this);
    }

    private static bool TryAssert(Action assertion)
    {
        try { assertion(); return true; }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed class HaveEnqueuedChain<TJob> : AndConstraint<FakeQueueServiceAssertions>
{
    public HaveEnqueuedChain(FakeQueueServiceAssertions assertions) : base(assertions) { }

    public AndConstraint<FakeQueueServiceAssertions> WithPayload<TPayload>(
        Func<TPayload, bool> predicate, string because = "", params object[] becauseArgs)
    {
        var matched = TryAssert(() => And.Subject.AssertEnqueued<TJob, TPayload>(predicate));
        Execute.Assertion
            .BecauseOf(because, becauseArgs)
            .ForCondition(matched)
            .FailWith(
                "Expected {context:FakeQueueService} to have enqueued a '{0}' job matching the predicate{reason}, but none matched.",
                typeof(TJob).Name);
        return new AndConstraint<FakeQueueServiceAssertions>(And);
    }

    private static bool TryAssert(Action assertion)
    {
        try { assertion(); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
