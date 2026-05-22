namespace DotInfraKit.Cache.Internal.Middleware;

internal sealed class CacheAutoInvalidationConfig
{
    internal string Prefix { get; }

    internal CacheAutoInvalidationConfig(string prefix) => Prefix = prefix;
}
