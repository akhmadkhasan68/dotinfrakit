using DotInfraKit.Queue;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Internal.Drivers;
using DotInfraKit.Queue.Internal.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotInfraKit;

public static class QueueServiceExtensions
{
    public static IServiceCollection AddJobQueue(this IServiceCollection services, Action<QueueOptions> configure)
    {
        var options = new QueueOptions();
        configure(options);

        var queueConfigs = new Dictionary<string, QueueConfiguration>();

        foreach (var builder in options.Queues)
        {
            var config = builder.Build();
            var registration = builder._driverRegistration ?? DefaultMemoryRegistration(config.ChannelCapacity);
            var capturedQueueName = config.QueueName;
            var capturedConfig = config;

            queueConfigs[capturedQueueName] = capturedConfig;

            if (registration.CreateDriverFromProvider is not null)
            {
                var capturedFactory = registration.CreateDriverFromProvider;
                services.AddKeyedSingleton<IQueueDriver>(capturedQueueName,
                    (sp, _) => capturedFactory(sp));
            }
            else
            {
                var driver = registration.CreateDriver
                    ?? throw new InvalidOperationException($"No driver configured for queue '{capturedQueueName}'.");
                var eagerDriver = driver();
                services.AddKeyedSingleton<IQueueDriver>(capturedQueueName, (_, _) => eagerDriver);
            }

            for (var i = 0; i < config.WorkerCount; i++)
            {
                var capturedWorkerId = $"{Environment.MachineName}:{capturedQueueName}:w{i}";
                services.AddHostedService(sp => new QueueWorkerService(
                    sp.GetRequiredKeyedService<IQueueDriver>(capturedQueueName),
                    capturedConfig,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ILogger<QueueWorkerService>>(),
                    capturedWorkerId,
                    config.WorkerCount));
            }

            services.AddHostedService(sp => new StuckJobSweeperService(
                sp.GetRequiredKeyedService<IQueueDriver>(capturedQueueName),
                capturedConfig,
                sp.GetRequiredService<ILogger<StuckJobSweeperService>>()));

            services.AddHostedService(sp => new DelayedJobSweeperService(
                sp.GetRequiredKeyedService<IQueueDriver>(capturedQueueName),
                capturedConfig,
                sp.GetRequiredService<ILogger<DelayedJobSweeperService>>()));

            if (config.DlqEnabled)
            {
                if (registration.CreateDlqServiceFromProvider is not null)
                {
                    var capturedDlqFactory = registration.CreateDlqServiceFromProvider;
                    services.AddSingleton<IDlqService>(sp =>
                        capturedDlqFactory(sp, sp.GetRequiredKeyedService<IQueueDriver>(capturedQueueName)));
                }
                else if (registration.CreateDlqService is not null)
                {
                    var capturedDlqFactory = registration.CreateDlqService;
                    services.AddSingleton<IDlqService>(sp =>
                        capturedDlqFactory(sp.GetRequiredKeyedService<IQueueDriver>(capturedQueueName)));
                }
            }

            foreach (var register in builder._startupRegistrations)
                register(services);
        }

        var capturedQueueConfigs = queueConfigs;

        var registry = new QueueRegistry(capturedQueueConfigs.Select(kvp => (kvp.Key, kvp.Value)));
        services.AddSingleton(registry);
        services.AddSingleton<IQueueMonitorService>(sp =>
            new QueueMonitorService(sp, sp.GetRequiredService<QueueRegistry>()));

        services.AddSingleton<IQueueService>(sp =>
        {
            var driverMap = capturedQueueConfigs.ToDictionary(
                kvp => kvp.Key,
                kvp => (sp.GetRequiredKeyedService<IQueueDriver>(kvp.Key), kvp.Value));
            return new QueueService(driverMap, sp.GetRequiredService<ILogger<QueueService>>());
        });

        return services;
    }

    private static QueueDriverRegistration DefaultMemoryRegistration(int capacity)
    {
        var dlqStore = new InMemoryDlqStore();
        return new QueueDriverRegistration
        {
            CreateDriver = () => new MemoryQueueDriver(capacity, dlqStore),
            CreateDlqService = driver => new InMemoryDlqService(dlqStore, driver)
        };
    }
}
