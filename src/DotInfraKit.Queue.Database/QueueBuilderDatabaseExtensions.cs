using System.Reflection;
using DotInfraKit.Queue.Internal;
using DotInfraKit.Queue.Internal.Configuration;
using DotInfraKit.Queue.Database.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotInfraKit.Queue.Database;

public static class QueueBuilderDatabaseExtensions
{
    public static QueueBuilder UseDatabaseDriver<TContext>(
        this QueueBuilder builder,
        Action<DatabaseDriverOptions>? configure = null)
        where TContext : DbContext
    {
        var opts = new DatabaseDriverOptions();
        configure?.Invoke(opts);

        var queueName = builder.QueueName;

        builder._driverRegistration = new QueueDriverRegistration
        {
            CreateDriverFromProvider = sp =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
                return new DatabaseQueueDriver<TContext>(factory, queueName);
            },
            CreateDlqServiceFromProvider = (sp, driver) =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<TContext>>();
                return new DatabaseDlqService<TContext>(factory, queueName, driver);
            }
        };

        if (opts.AutoMigrateEnabled)
        {
            builder._startupRegistrations.Add(services =>
                services.AddHostedService(sp => new DatabaseQueueMigratorService<TContext>(
                    sp.GetRequiredService<IDbContextFactory<TContext>>(),
                    sp.GetRequiredService<IHostEnvironment>(),
                    opts)));
        }

        return builder;
    }
}
