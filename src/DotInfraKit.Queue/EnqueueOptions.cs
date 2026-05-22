namespace DotInfraKit.Queue;

public sealed class EnqueueOptions
{
    public int Priority { get; set; } = 0;
    public TimeSpan? Delay { get; set; }
    public DateTime? RunAt { get; set; }
}
