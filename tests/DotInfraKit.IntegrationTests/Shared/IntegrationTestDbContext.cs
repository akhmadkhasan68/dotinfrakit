using DotInfraKit.Queue.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotInfraKit.IntegrationTests;

internal sealed class IntegrationTestDbContext(DbContextOptions<IntegrationTestDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.AddDotInfraKitQueue();
}

internal sealed class IntegrationTestDbContextFactory : IDbContextFactory<IntegrationTestDbContext>
{
    private readonly string _connectionString;

    public IntegrationTestDbContextFactory(string connectionString)
        => _connectionString = connectionString;

    public IntegrationTestDbContextFactory(SqliteConnection sharedConnection)
        => _connectionString = sharedConnection.ConnectionString;

    private bool _useSharedConnection;
    private SqliteConnection? _sharedConnection;

    public static IntegrationTestDbContextFactory ForSharedConnection(SqliteConnection conn)
        => new(conn) { _useSharedConnection = true, _sharedConnection = conn };

    public IntegrationTestDbContext CreateDbContext()
    {
        DbContextOptionsBuilder<IntegrationTestDbContext> opts = new();

        if (_useSharedConnection && _sharedConnection is not null)
            opts.UseSqlite(_sharedConnection);
        else
            opts.UseSqlite(_connectionString);

        return new IntegrationTestDbContext(opts.Options);
    }

    public Task<IntegrationTestDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
