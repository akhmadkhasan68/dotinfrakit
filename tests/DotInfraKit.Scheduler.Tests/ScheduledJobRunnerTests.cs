using DotInfraKit.Scheduler;
using DotInfraKit.Scheduler.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;

namespace DotInfraKit.Scheduler.Tests;

public class ScheduledJobRunnerTests
{
    // Concrete test job — required because NSubstitute proxy types are not resolvable via Type.GetType()
    private sealed class CapturingJob : IScheduledJob
    {
        public bool WasCalled { get; private set; }
        public Exception? ThrowOnExecute { get; set; }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (ThrowOnExecute is not null)
                throw ThrowOnExecute;
            return Task.CompletedTask;
        }
    }

    private static IJobExecutionContext BuildContext(Type jobType)
    {
        var dataMap = new JobDataMap { ["JobType"] = jobType.AssemblyQualifiedName! };
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.JobDataMap.Returns(dataMap);
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task Execute_CallsJobExecuteAsync()
    {
        var job = new CapturingJob();
        var services = new ServiceCollection();
        services.AddSingleton<CapturingJob>(job);
        var sp = services.BuildServiceProvider();

        var runner = new ScheduledJobRunner(sp, NullLogger<ScheduledJobRunner>.Instance);
        await runner.Execute(BuildContext(typeof(CapturingJob)));

        job.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_WhenJobThrows_DoesNotRethrow()
    {
        var job = new CapturingJob { ThrowOnExecute = new InvalidOperationException("boom") };
        var services = new ServiceCollection();
        services.AddSingleton<CapturingJob>(job);
        var sp = services.BuildServiceProvider();

        var runner = new ScheduledJobRunner(sp, NullLogger<ScheduledJobRunner>.Instance);

        var act = async () => await runner.Execute(BuildContext(typeof(CapturingJob)));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Execute_WhenJobThrows_InvokesExceptionHandler()
    {
        var exception = new InvalidOperationException("boom");
        var job = new CapturingJob { ThrowOnExecute = exception };
        var handler = Substitute.For<IScheduledJobExceptionHandler>();

        var services = new ServiceCollection();
        services.AddSingleton<CapturingJob>(job);
        services.AddSingleton(handler);
        var sp = services.BuildServiceProvider();

        var runner = new ScheduledJobRunner(sp, NullLogger<ScheduledJobRunner>.Instance);
        await runner.Execute(BuildContext(typeof(CapturingJob)));

        await handler.Received(1).HandleAsync(typeof(CapturingJob), exception, CancellationToken.None);
    }

    [Fact]
    public async Task Execute_WhenJobTypeUnresolvable_ThrowsInvalidOperationException()
    {
        var dataMap = new JobDataMap { ["JobType"] = "NonExistent.Type, NonExistentAssembly" };
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.JobDataMap.Returns(dataMap);
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.CancellationToken.Returns(CancellationToken.None);

        var runner = new ScheduledJobRunner(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<ScheduledJobRunner>.Instance);

        var act = async () => await runner.Execute(context);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be resolved*");
    }
}
