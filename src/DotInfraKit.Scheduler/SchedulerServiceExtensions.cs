using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using DotInfraKit.Scheduler;
using DotInfraKit.Scheduler.Internal;

namespace DotInfraKit;

public static class SchedulerServiceExtensions
{
    public static IServiceCollection AddJobScheduler(
        this IServiceCollection services,
        Action<JobSchedulerBuilder> configure)
    {
        var builder = new JobSchedulerBuilder();
        configure(builder);

        // Temporary SP to resolve IConfiguration for WithCronFromConfig resolution at startup
        var config = services.BuildServiceProvider().GetService<IConfiguration>();

        services.AddQuartz(q =>
        {
            if (builder.ClusterOptions is { } cluster)
                ConfigureClusterMode(q, cluster, services);

            foreach (var (jobType, schedule) in builder.Jobs)
            {
                var cronExpr = schedule.ToCronExpression(config);
                var jobKey = JobKey.Create(jobType.Name, "DotInfraKit");

                q.AddJob<ScheduledJobRunner>(jobKey, j => j
                    .UsingJobData("JobType", jobType.AssemblyQualifiedName!)
                    .StoreDurably());

                q.AddTrigger(t => t
                    .ForJob(jobKey)
                    .WithCronSchedule(cronExpr)
                    .WithIdentity($"{jobType.Name}-trigger", "DotInfraKit"));

                services.AddScoped(jobType);
            }
        });

        services.AddQuartzHostedService(opts =>
        {
            var raw = config?["DotInfraKit:Scheduler:WaitForJobsToComplete"];
            opts.WaitForJobsToComplete = !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
        });

        return services;
    }

    private static void ConfigureClusterMode(
        IServiceCollectionQuartzConfigurator q,
        ClusterOptions cluster,
        IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(cluster.ConnectionString))
            throw new InvalidOperationException(
                "Cluster mode requires a connection string. Call cluster.UseDatabaseStore(connectionString).");

        var provider = cluster.Provider == QuartzDbProvider.AutoDetect
            ? DetectProvider(services)
            : cluster.Provider;

        q.UsePersistentStore(store =>
        {
            store.UseProperties = true;
            store.RetryInterval = TimeSpan.FromSeconds(15);

            var cs = cluster.ConnectionString;
            switch (provider)
            {
                case QuartzDbProvider.SqlServer:
                    store.UseSqlServer(cs);
                    break;
                case QuartzDbProvider.PostgreSQL:
                    store.UsePostgres(cs);
                    break;
                case QuartzDbProvider.MySQL:
                    store.UseMySql(cs);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Quartz DB provider: {provider}. Use SqlServer, PostgreSQL, or MySQL.");
            }

            store.UseNewtonsoftJsonSerializer();
        });

        q.Properties["quartz.scheduler.instanceId"] = cluster.InstanceId;
        q.Properties["quartz.jobStore.clustered"] = "true";
        q.SchedulerName = "DotInfraKit";
    }

    private static QuartzDbProvider DetectProvider(IServiceCollection services)
    {
        // Use reflection to avoid a hard EF Core dependency in the Scheduler project.
        // Inspect registered DbContextOptions<> service to read the provider extension type names.
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType.IsGenericType &&
            d.ServiceType.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.DbContextOptions`1");

        if (descriptor is null)
            throw new InvalidOperationException(
                "Cluster mode AutoDetect failed: no DbContext is registered. " +
                "Call cluster.UseDatabaseStore(connectionString, QuartzDbProvider.X) to specify the provider explicitly.");

        using var sp = services.BuildServiceProvider();
        var options = sp.GetService(descriptor.ServiceType);

        if (options is null)
            throw new InvalidOperationException(
                "Cluster mode AutoDetect failed: DbContextOptions could not be resolved. " +
                "Specify the provider explicitly via QuartzDbProvider.");

        var extensionsProp = options.GetType().GetProperty("Extensions");
        var extensions = extensionsProp?.GetValue(options) as System.Collections.IEnumerable;

        foreach (var ext in extensions ?? Array.Empty<object>())
        {
            var typeName = ext?.GetType().FullName ?? string.Empty;
            if (typeName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return QuartzDbProvider.SqlServer;
            if (typeName.Contains("Npgsql",    StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Postgres",  StringComparison.OrdinalIgnoreCase)) return QuartzDbProvider.PostgreSQL;
            if (typeName.Contains("MySql",     StringComparison.OrdinalIgnoreCase)) return QuartzDbProvider.MySQL;
        }

        throw new InvalidOperationException(
            "Cluster mode AutoDetect failed: could not determine EF Core provider from registered DbContext. " +
            "Call cluster.UseDatabaseStore(connectionString, QuartzDbProvider.X) explicitly.");
    }
}
