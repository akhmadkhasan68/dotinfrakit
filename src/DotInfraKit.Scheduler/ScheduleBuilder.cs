using Microsoft.Extensions.Configuration;

namespace DotInfraKit.Scheduler;

public sealed class ScheduleBuilder
{
    private enum ScheduleType { None, EveryMinute, EveryMinutes, Hourly, Daily, Weekly, Monthly, RawCron, ConfigCron }

    private ScheduleType _type = ScheduleType.None;
    private int _n;
    private int _hour;
    private int _minute;
    private DayOfWeek _day = DayOfWeek.Monday;
    private string? _rawCron;
    private string? _configKey;

    public ScheduleBuilder EveryMinute()
    {
        _type = ScheduleType.EveryMinute;
        return this;
    }

    public ScheduleBuilder EveryMinutes(int n)
    {
        _type = ScheduleType.EveryMinutes;
        _n = n;
        return this;
    }

    public ScheduleBuilder Hourly()
    {
        _type = ScheduleType.Hourly;
        return this;
    }

    public ScheduleBuilder Daily()
    {
        _type = ScheduleType.Daily;
        return this;
    }

    public ScheduleBuilder Weekly()
    {
        _type = ScheduleType.Weekly;
        return this;
    }

    public ScheduleBuilder Monthly()
    {
        _type = ScheduleType.Monthly;
        return this;
    }

    public ScheduleBuilder At(int hour, int minute)
    {
        _hour = hour;
        _minute = minute;
        return this;
    }

    public ScheduleBuilder On(DayOfWeek day)
    {
        _day = day;
        return this;
    }

    public ScheduleBuilder WithCron(string expression)
    {
        _type = ScheduleType.RawCron;
        _rawCron = expression;
        return this;
    }

    public ScheduleBuilder WithCronFromConfig(string configKey)
    {
        _type = ScheduleType.ConfigCron;
        _configKey = configKey;
        return this;
    }

    public string ToCronExpression(IConfiguration? config = null) => _type switch
    {
        ScheduleType.EveryMinute  => "0 * * * * ?",
        ScheduleType.EveryMinutes => $"0 */{_n} * * * ?",
        ScheduleType.Hourly       => "0 0 * * * ?",
        ScheduleType.Daily        => $"0 {_minute} {_hour} * * ?",
        ScheduleType.Weekly       => $"0 {_minute} {_hour} ? * {ToQuartzDay(_day)}",
        ScheduleType.Monthly      => $"0 {_minute} {_hour} 1 * ?",
        ScheduleType.RawCron      => _rawCron!,
        ScheduleType.ConfigCron   => ResolveFromConfig(config),
        _ => throw new InvalidOperationException(
            "No schedule configured. Call one of: EveryMinute(), Daily(), Weekly(), Monthly(), WithCron(), etc.")
    };

    private string ResolveFromConfig(IConfiguration? config)
    {
        var value = config?[_configKey!];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Cron expression for job could not be found at config key '{_configKey}'. " +
                "Ensure the key exists in appsettings.json.");
        return value;
    }

    private static string ToQuartzDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday    => "MON",
        DayOfWeek.Tuesday   => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday  => "THU",
        DayOfWeek.Friday    => "FRI",
        DayOfWeek.Saturday  => "SAT",
        DayOfWeek.Sunday    => "SUN",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null)
    };
}
