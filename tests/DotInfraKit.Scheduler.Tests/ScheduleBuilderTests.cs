using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DotInfraKit.Scheduler.Tests;

public class ScheduleBuilderTests
{
    [Theory]
    [InlineData(nameof(EveryMinute),      "0 * * * * ?")]
    [InlineData(nameof(EveryMinutes15),   "0 */15 * * * ?")]
    [InlineData(nameof(Hourly),           "0 0 * * * ?")]
    [InlineData(nameof(DailyDefault),     "0 0 0 * * ?")]
    [InlineData(nameof(DailyAt7),         "0 0 7 * * ?")]
    [InlineData(nameof(WeeklyMonAt630),   "0 30 6 ? * MON")]
    [InlineData(nameof(MonthlyAt0),       "0 0 0 1 * ?")]
    [InlineData(nameof(RawCron),          "0 15 10 ? * MON-FRI")]
    public void ToCronExpression_ReturnsCorrectCron(string scenario, string expected)
    {
        var builder = scenario switch
        {
            nameof(EveryMinute)    => EveryMinute(),
            nameof(EveryMinutes15) => EveryMinutes15(),
            nameof(Hourly)         => Hourly(),
            nameof(DailyDefault)   => DailyDefault(),
            nameof(DailyAt7)       => DailyAt7(),
            nameof(WeeklyMonAt630) => WeeklyMonAt630(),
            nameof(MonthlyAt0)     => MonthlyAt0(),
            nameof(RawCron)        => RawCron(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        builder.ToCronExpression().Should().Be(expected);
    }

    [Fact]
    public void ToCronExpression_WithCronFromConfig_ResolvesValueFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Audit:Cron"] = "0 0 2 * * ?" })
            .Build();

        var cron = new ScheduleBuilder()
            .WithCronFromConfig("Jobs:Audit:Cron")
            .ToCronExpression(config);

        cron.Should().Be("0 0 2 * * ?");
    }

    [Fact]
    public void ToCronExpression_WithCronFromConfig_ThrowsWhenKeyMissing()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => new ScheduleBuilder()
            .WithCronFromConfig("Missing:Key")
            .ToCronExpression(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing:Key*");
    }

    [Fact]
    public void ToCronExpression_WithNoScheduleConfigured_Throws()
    {
        var act = () => new ScheduleBuilder().ToCronExpression();

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(DayOfWeek.Monday,    "MON")]
    [InlineData(DayOfWeek.Tuesday,   "TUE")]
    [InlineData(DayOfWeek.Wednesday, "WED")]
    [InlineData(DayOfWeek.Thursday,  "THU")]
    [InlineData(DayOfWeek.Friday,    "FRI")]
    [InlineData(DayOfWeek.Saturday,  "SAT")]
    [InlineData(DayOfWeek.Sunday,    "SUN")]
    public void Weekly_OnDay_ProducesCorrectDayPart(DayOfWeek day, string expectedDay)
    {
        var cron = new ScheduleBuilder().Weekly().On(day).At(0, 0).ToCronExpression();

        cron.Should().Contain(expectedDay);
    }

    // Scenario builders — static so InlineData strings can reference them by name
    private static ScheduleBuilder EveryMinute()    => new ScheduleBuilder().EveryMinute();
    private static ScheduleBuilder EveryMinutes15() => new ScheduleBuilder().EveryMinutes(15);
    private static ScheduleBuilder Hourly()         => new ScheduleBuilder().Hourly();
    private static ScheduleBuilder DailyDefault()   => new ScheduleBuilder().Daily();
    private static ScheduleBuilder DailyAt7()       => new ScheduleBuilder().Daily().At(7, 0);
    private static ScheduleBuilder WeeklyMonAt630() => new ScheduleBuilder().Weekly().On(DayOfWeek.Monday).At(6, 30);
    private static ScheduleBuilder MonthlyAt0()     => new ScheduleBuilder().Monthly().At(0, 0);
    private static ScheduleBuilder RawCron()        => new ScheduleBuilder().WithCron("0 15 10 ? * MON-FRI");
}
