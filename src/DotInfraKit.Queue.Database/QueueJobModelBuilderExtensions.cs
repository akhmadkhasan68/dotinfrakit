using DotInfraKit.Queue.Database.Internal;
using Microsoft.EntityFrameworkCore;

namespace DotInfraKit.Queue.Database;

public static class QueueJobModelBuilderExtensions
{
    public static ModelBuilder AddDotInfraKitQueue(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueueJobRecord>(e =>
        {
            e.ToTable("dotinfrakit_queue_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.QueueName).HasColumnName("queue_name").HasMaxLength(255).IsRequired();
            e.Property(x => x.JobType).HasColumnName("job_type").HasMaxLength(500).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(50).IsRequired().HasDefaultValue("pending");
            e.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
            e.Property(x => x.MaxAttempts).HasColumnName("max_attempts").HasDefaultValue(3);
            e.Property(x => x.Priority).HasColumnName("priority").HasDefaultValue(0);
            e.Property(x => x.NextRunAt).HasColumnName("next_run_at");
            e.Property(x => x.LockedAt).HasColumnName("locked_at");
            e.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(255);
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");

            e.HasIndex(x => new { x.Status, x.Priority, x.NextRunAt })
                .HasDatabaseName("IX_dotinfrakit_queue_jobs_dequeue");
            e.HasIndex(x => x.QueueName)
                .HasDatabaseName("IX_dotinfrakit_queue_jobs_queue_name");
        });

        return modelBuilder;
    }
}
