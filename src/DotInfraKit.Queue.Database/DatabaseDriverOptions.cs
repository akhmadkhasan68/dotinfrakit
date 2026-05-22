namespace DotInfraKit.Queue.Database;

public sealed class DatabaseDriverOptions
{
    internal bool AutoMigrateEnabled { get; private set; }
    internal bool AllowAutoMigrateInProduction { get; private set; }

    public DatabaseDriverOptions AutoMigrate(bool allowInProduction = false)
    {
        AutoMigrateEnabled = true;
        AllowAutoMigrateInProduction = allowInProduction;
        return this;
    }
}
