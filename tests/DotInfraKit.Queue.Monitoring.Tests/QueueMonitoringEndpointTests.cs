using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotInfraKit.Queue;
using DotInfraKit.Queue.Monitoring;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotInfraKit.Queue.Monitoring.Tests;

file sealed class TestJob : IQueueJob<string>
{
    public Task ExecuteAsync(string payload, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public class QueueMonitoringEndpointTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> CreateClientAsync(
        Action<QueueMonitoringOptions>? configureMonitoring = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddJobQueue(opts =>
        {
            opts.UseDefaultQueue(q => q.Workers(count: 0));
            opts.AddQueue("emails", q => q.Workers(count: 0));
        });
        builder.Services.AddScoped<TestJob>();
        builder.Services.AddQueueMonitoring(configureMonitoring);
        configureServices?.Invoke(builder.Services);

        _app = builder.Build();
        _app.MapQueueMonitoring();
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private async Task EnqueueAsync(string queueName, string payload = "test")
    {
        var queue = _app!.Services.GetRequiredService<IQueueService>();
        await queue.EnqueueAsync<TestJob, string>(queueName, payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.StopAsync();
    }

    private static JsonDocument ParseBody(string content)
        => JsonDocument.Parse(content);

    // ── List endpoint ────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsOk_WithEmptyQueue()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/queue-monitoring/queues");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_WithEnqueuedJob_ReturnsThatJob()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_WithNameFilter_ReturnsOnlyMatchingQueue()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default");
        await EnqueueAsync("emails");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        var first = body.RootElement.GetProperty("data").EnumerateArray().First();
        first.GetProperty("queueName").GetString().Should().Be("default");
    }

    [Fact]
    public async Task List_WithNonExistentNameFilter_ReturnsEmptyData()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/queue-monitoring/queues?name=nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_WithStatusFilter_ReturnsOnlyMatchingStatus()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default&status=pending");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        var first = body.RootElement.GetProperty("data").EnumerateArray().First();
        first.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task List_WithUnknownStatusFilter_ReturnsEmptyData()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default&status=unknown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task List_WithPagination_ReturnsCorrectPage()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default", "job1");
        await EnqueueAsync("default", "job2");
        await EnqueueAsync("default", "job3");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default&page=1&pageSize=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
        body.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);
        body.RootElement.GetProperty("pageSize").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task List_JobObject_ContainsExpectedFields()
    {
        var client = await CreateClientAsync();
        await EnqueueAsync("default");

        var response = await client.GetAsync("/queue-monitoring/queues?name=default");

        var body = ParseBody(await response.Content.ReadAsStringAsync());
        var job = body.RootElement.GetProperty("data").EnumerateArray().First();
        job.TryGetProperty("id", out _).Should().BeTrue();
        job.GetProperty("queueName").GetString().Should().Be("default");
        job.TryGetProperty("jobType", out _).Should().BeTrue();
        job.TryGetProperty("payload", out _).Should().BeTrue();
        job.TryGetProperty("status", out _).Should().BeTrue();
        job.TryGetProperty("attempts", out _).Should().BeTrue();
    }

    // ── Stats endpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_ReturnsOk_WithAllQueues()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/queue-monitoring/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = ParseBody(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue();
        body.RootElement.GetProperty("queues").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Stats_ContainsCountFields()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/queue-monitoring/stats");

        var body = ParseBody(await response.Content.ReadAsStringAsync());
        var queue = body.RootElement.GetProperty("queues").EnumerateArray()
            .First(q => q.GetProperty("name").GetString() == "default");
        queue.TryGetProperty("pending", out _).Should().BeTrue();
        queue.TryGetProperty("processing", out _).Should().BeTrue();
        queue.TryGetProperty("delayed", out _).Should().BeTrue();
        queue.TryGetProperty("deadLetter", out _).Should().BeTrue();
    }

    // ── Detail endpoint ──────────────────────────────────────────────────────

    [Fact]
    public async Task Detail_UnknownQueue_Returns404()
    {
        var client = await CreateClientAsync();
        var fakeId = Guid.NewGuid();

        var response = await client.GetAsync($"/queue-monitoring/queues/nonexistent/{fakeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Detail_UnknownJobId_Returns404()
    {
        var client = await CreateClientAsync();
        var fakeId = Guid.NewGuid();

        var response = await client.GetAsync($"/queue-monitoring/queues/default/{fakeId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Custom path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomPath_EndpointsRespond_OnNewPath()
    {
        var client = await CreateClientAsync(opts => opts.Path = "/infra/queues");

        var listResponse = await client.GetAsync("/infra/queues/queues");
        var statsResponse = await client.GetAsync("/infra/queues/stats");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        statsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CustomPath_DefaultPath_Returns404()
    {
        var client = await CreateClientAsync(opts => opts.Path = "/infra/queues");

        var response = await client.GetAsync("/queue-monitoring/queues");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Basic Auth ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BasicAuth_NoHeader_Returns401()
    {
        var client = await CreateClientAsync(opts =>
            opts.BasicAuth = new BasicAuthOptions { Username = "admin", Password = "secret" });

        var listResponse  = await client.GetAsync("/queue-monitoring/queues");
        var statsResponse = await client.GetAsync("/queue-monitoring/stats");
        var detailResponse = await client.GetAsync($"/queue-monitoring/queues/default/{Guid.NewGuid()}");

        listResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        statsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        detailResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BasicAuth_WrongCredentials_Returns401()
    {
        var client = await CreateClientAsync(opts =>
            opts.BasicAuth = new BasicAuthOptions { Username = "admin", Password = "secret" });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong")));

        var response = await client.GetAsync("/queue-monitoring/queues");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BasicAuth_CorrectCredentials_Returns200()
    {
        var client = await CreateClientAsync(opts =>
            opts.BasicAuth = new BasicAuthOptions { Username = "admin", Password = "secret" });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")));

        var listResponse  = await client.GetAsync("/queue-monitoring/queues");
        var statsResponse = await client.GetAsync("/queue-monitoring/stats");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        statsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
