using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace DotInfraKit.Queue.Database.Internal;

internal sealed class DatabaseQueueMigratorService<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly IHostEnvironment _env;
    private readonly DatabaseDriverOptions _opts;

    public DatabaseQueueMigratorService(
        IDbContextFactory<TContext> factory,
        IHostEnvironment env,
        DatabaseDriverOptions opts)
    {
        _factory = factory;
        _env = env;
        _opts = opts;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_opts.AutoMigrateEnabled) return;

        if (!_opts.AllowAutoMigrateInProduction && _env.IsProduction())
            throw new InvalidOperationException(
                "AutoMigrate is disabled in production environments. " +
                "Call AutoMigrate(allowInProduction: true) to override, or run migrations manually.");

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        await ctx.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
