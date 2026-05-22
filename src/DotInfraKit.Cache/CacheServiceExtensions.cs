using DotInfraKit.Cache;
using DotInfraKit.Cache.Internal.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit;

public static class CacheServiceExtensions
{
    public static IServiceCollection AddAppCache(this IServiceCollection services, Action<CacheBuilder> configure)
    {
        var builder = new CacheBuilder();
        configure(builder);

        var factory = builder.DriverFactory
            ?? throw new InvalidOperationException("Call UseMemory(), UseRedis(), or another driver method on CacheBuilder.");

        if (builder._isMemory)
            services.AddMemoryCache();

        services.AddSingleton<ICacheService>(sp => factory(sp));

        if (builder.AutoInvalidationPrefix is not null)
            services.AddSingleton(new CacheAutoInvalidationConfig(builder.AutoInvalidationPrefix));

        return services;
    }

    public static IApplicationBuilder UseAppCache(this IApplicationBuilder app)
    {
        var config = app.ApplicationServices.GetService<CacheAutoInvalidationConfig>();
        if (config is null)
            return app;

        var cache = app.ApplicationServices.GetRequiredService<ICacheService>();
        var prefix = config.Prefix;

        return app.Use(next => async context =>
        {
            await next(context);

            if (HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method) ||
                HttpMethods.IsDelete(context.Request.Method))
            {
                await cache.ForgetByPrefixAsync(prefix);
            }
        });
    }
}
