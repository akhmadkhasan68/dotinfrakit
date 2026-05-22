namespace DotInfraKit.Queue;

public interface IDlqService
{
    Task<IReadOnlyList<DlqJobRecord>> GetDeadJobsAsync(string queueName, int page = 1, int pageSize = 20);
    Task<Guid> RetryAsync(Guid jobId);
    Task RetryAllAsync(string queueName);
    Task DeleteAsync(Guid jobId);
    Task DeleteAllAsync(string queueName);
}
