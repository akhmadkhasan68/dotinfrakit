using DotInfraKit.Queue;
using DotInfraKit.Queue.Monitoring.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit.Queue.Monitoring;

public static class QueueMonitoringExtensions
{
    public static IServiceCollection AddQueueMonitoring(
        this IServiceCollection services,
        Action<QueueMonitoringOptions>? configure = null)
    {
        var opts = new QueueMonitoringOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);
        return services;
    }

    public static IEndpointConventionBuilder MapQueueMonitoring(this IEndpointRouteBuilder endpoints)
    {
        var opts = endpoints.ServiceProvider.GetRequiredService<QueueMonitoringOptions>();

        Func<HttpContext, bool> authCheck = opts.BasicAuth is not null
            ? ctx => BasicAuthHelper.Validate(ctx, opts.BasicAuth)
            : _ => true;

        var group = endpoints.MapGroup(opts.Path)
            .WithTags("Infrastructure");

        group.MapGet("/queues",
            async (IQueueMonitorService monitor, HttpContext ctx, CancellationToken ct,
                   int page = 1, int pageSize = 20, string? name = null, string? status = null) =>
            {
                if (!authCheck(ctx)) return Results.Unauthorized();
                page = Math.Max(1, page);
                pageSize = Math.Clamp(pageSize, 1, 100);
                var (items, total) = await monitor.ListJobsAsync(name, status, page, pageSize, ct);
                return Results.Ok(new QueueListResponse(page, pageSize, (int)total, items));
            })
            .WithName("QueueMonitoringList")
            .Produces<QueueListResponse>();

        group.MapGet("/stats",
            async (IQueueMonitorService monitor, HttpContext ctx, CancellationToken ct) =>
            {
                if (!authCheck(ctx)) return Results.Unauthorized();
                var all = await monitor.GetAllStatsAsync(ct);
                return Results.Ok(new QueueStatsResponse(DateTime.UtcNow, all));
            })
            .WithName("QueueMonitoringStats")
            .Produces<QueueStatsResponse>();

        group.MapGet("/queues/{name}/{id:guid}",
            async (string name, Guid id, IQueueMonitorService monitor, HttpContext ctx, CancellationToken ct) =>
            {
                if (!authCheck(ctx)) return Results.Unauthorized();
                var job = await monitor.GetJobAsync(name, id, ct);
                return job is null ? Results.NotFound() : Results.Ok(job);
            })
            .WithName("QueueMonitoringDetail")
            .Produces<QueueJobDetail>()
            .Produces(StatusCodes.Status404NotFound);

        if (opts.RequireAuthorization)
        {
            if (opts.AuthorizationPolicy is not null)
                group.RequireAuthorization(opts.AuthorizationPolicy);
            else
                group.RequireAuthorization();
        }

        return group;
    }

    public static IApplicationBuilder UseQueueMonitoring(this IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints => endpoints.MapQueueMonitoring());
        return app;
    }
}
