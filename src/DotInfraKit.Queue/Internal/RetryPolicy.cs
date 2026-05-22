namespace DotInfraKit.Queue.Internal;

internal sealed class RetryPolicy
{
    public BackoffType BackoffType { get; init; } = BackoffType.Exponential;
    public int InitialDelayMs { get; init; } = 2000;

    public TimeSpan CalculateDelay(int attempt) => BackoffType switch
    {
        BackoffType.Exponential => TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * InitialDelayMs),
        BackoffType.Fixed       => TimeSpan.FromMilliseconds(InitialDelayMs),
        BackoffType.Linear      => TimeSpan.FromMilliseconds(attempt * InitialDelayMs),
        _ => throw new ArgumentOutOfRangeException(nameof(BackoffType))
    };
}
