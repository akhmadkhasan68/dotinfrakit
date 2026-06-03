# DotInfraKit

Infrastructure building blocks for .NET 8+ — background job scheduling, durable job queues with retry and dead-letter queues, and multi-backend caching, all wired up with a single fluent DI registration.

## Packages

| Package | Description |
|---------|-------------|
| `DotInfraKit.Scheduler` | Cron-based job scheduler powered by Quartz.NET, with cluster mode |
| `DotInfraKit.Queue` | Background job queue — retry policies, dead-letter queue, named queues |
| `DotInfraKit.Queue.Redis` | Redis driver for `DotInfraKit.Queue` |
| `DotInfraKit.Queue.Database` | EF Core database driver for `DotInfraKit.Queue` |
| `DotInfraKit.Queue.Monitoring` | HTTP monitoring endpoints for queues — list, live stats, and job detail |
| `DotInfraKit.Cache` | Caching abstraction — Memory, Redis, Sentinel, and Cluster backends |

## Requirements

- .NET 8.0+

## Install

```
dotnet add package DotInfraKit
```

---

## A. Scheduler

### 1. Register in `Program.cs`

```csharp
using DotInfraKit;
using DotInfraKit.Scheduler;

builder.Services.AddJobScheduler(scheduler =>
{
    scheduler.Schedule<DailyReportJob>().Daily();
    scheduler.Schedule<HourlyMetricsJob>().WithCron("0 * * * *");
    scheduler.Schedule<AlertJob>().WithCronFromConfig("Jobs:AlertCron"); // from appsettings.json
});
```

### 2. Define a job

Jobs are resolved from the DI container on every execution — constructor injection works normally.

```csharp
using DotInfraKit.Scheduler;

public class DailyReportJob : IScheduledJob
{
    private readonly IReportService _reports;
    private readonly ILogger<DailyReportJob> _logger;

    public DailyReportJob(IReportService reports, ILogger<DailyReportJob> logger)
    {
        _reports = reports;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating daily report...");
        await _reports.GenerateDailyAsync(cancellationToken);
    }
}
```

### 3. Schedule reference

| Builder method | Fires |
|----------------|-------|
| `.Daily()` | Every day at midnight UTC |
| `.Weekly()` | Every Monday at midnight UTC |
| `.Monthly()` | First day of each month at midnight UTC |
| `.WithCron("0 * * * *")` | Custom 5-field cron expression |
| `.WithCronFromConfig("Section:Key")` | Reads cron expression from `IConfiguration` at startup |

---

### Cluster mode

Cluster mode prevents the same job from running on more than one instance at a time. It requires a shared relational database with the Quartz.NET schema applied — see the [Quartz DDL scripts](https://github.com/quartznet/quartznet/tree/main/database/tables) for your database provider.

**A.1 — Apply the Quartz DDL** to your shared database (run once, before deploying).

**A.2 — Enable cluster mode** in the registration:

```csharp
builder.Services.AddJobScheduler(scheduler =>
{
    scheduler.UseClusterMode(cluster =>
    {
        cluster.UseDatabaseStore(connectionString, QuartzDbProvider.SqlServer);
        cluster.InstanceId = Environment.MachineName; // must be unique per instance
    });

    scheduler.Schedule<DailyReportJob>().Daily();
});
```

---

## B. Queue

### 1. Register in `Program.cs`

```csharp
using DotInfraKit;
using DotInfraKit.Queue;
using DotInfraKit.Queue.Redis;

builder.Services.AddJobQueue(options =>
    options.UseDefaultQueue(q =>
    {
        q.UseRedis(r =>
        {
            r.Endpoint = "localhost:6379";
            r.Password = "your-password";   // required in production
        });
        q.Workers(concurrency: 4);
        q.Retry(maxAttempts: 3, BackoffType.Exponential, initialDelayMs: 500);
        q.EnableDeadLetterQueue();
    }));
```

### 2. Define a payload and job

```csharp
using DotInfraKit.Queue;

// Payload — any serializable type
public record EmailPayload(string To, string Subject, string Body);

// Job — resolved from DI on every execution
public class SendEmailJob : IQueueJob<EmailPayload>
{
    private readonly IEmailSender _sender;
    private readonly ILogger<SendEmailJob> _logger;

    public SendEmailJob(IEmailSender sender, ILogger<SendEmailJob> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        EmailPayload payload,
        JobContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Sending to {To} — attempt {Attempt}/{Max}",
            payload.To, context.AttemptNumber, context.MaxAttempts);

        await _sender.SendAsync(payload.To, payload.Subject, payload.Body, cancellationToken);
    }
}
```

> **Important:** Every job class must be registered in the DI container. The queue worker resolves jobs at runtime via `GetRequiredService` — if the type is not registered you will get a runtime exception.
>
> ```csharp
> // In Program.cs — register each job class
> builder.Services.AddScoped<SendEmailJob>();
> ```

**`JobContext` properties available inside `ExecuteAsync`:**

| Property | Type | Description |
|----------|------|-------------|
| `JobId` | `Guid` | Unique identifier for this job instance |
| `QueueName` | `string` | Queue this job was enqueued on |
| `AttemptNumber` | `int` | Current attempt — starts at 1 |
| `MaxAttempts` | `int` | Total allowed attempts before DLQ |
| `EnqueuedAt` | `DateTime` | UTC time the job was first enqueued |

### 3. Enqueue jobs

Inject `IQueueService` via constructor injection wherever you need to schedule work.

```csharp
public class OrderService
{
    private readonly IQueueService _queue;

    public OrderService(IQueueService queue) => _queue = queue;

    public async Task PlaceOrderAsync(Order order)
    {
        // Execute immediately
        await _queue.EnqueueAsync<SendEmailJob, EmailPayload>(
            new EmailPayload(order.CustomerEmail, "Order confirmed", $"Order #{order.Id} is on its way."));

        // Execute after a delay
        await _queue.EnqueueAsync<SendEmailJob, EmailPayload>(
            new EmailPayload(order.CustomerEmail, "How was your order?", "We'd love your feedback."),
            new EnqueueOptions { Delay = TimeSpan.FromDays(3) });

        // Execute at a specific UTC time
        await _queue.EnqueueAsync<SendEmailJob, EmailPayload>(
            new EmailPayload(order.CustomerEmail, "Reminder", "Your cart is waiting."),
            new EnqueueOptions { RunAt = DateTime.UtcNow.AddHours(24) });
    }
}
```

### 4. Inspect and retry failed jobs

When a job fails all retry attempts it moves to the dead-letter queue (DLQ). Inject `IDlqService` to inspect and reprocess failed jobs.

```csharp
public class FailedJobsService
{
    private readonly IDlqService _dlq;

    public FailedJobsService(IDlqService dlq) => _dlq = dlq;

    public async Task ShowFailedAsync()
    {
        var jobs = await _dlq.GetDeadJobsAsync("default", page: 1, pageSize: 20);

        foreach (var job in jobs)
            Console.WriteLine($"[{job.DeadAt:u}] {job.JobType} — {job.Attempts} attempts — {job.ErrorMessage}");
    }

    public async Task RetryAllAsync()
    {
        await _dlq.RetryAllAsync("default"); // re-enqueues every failed job
    }

    public async Task CleanupAsync(Guid jobId)
    {
        await _dlq.DeleteAsync(jobId);       // remove single entry
        await _dlq.DeleteAllAsync("default"); // remove all entries
    }
}
```

**`DlqJobRecord` fields:**

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `Guid` | Job identifier |
| `QueueName` | `string` | Queue the job was on |
| `JobType` | `string` | Assembly-qualified job type name |
| `Payload` | `string` | Serialized JSON payload |
| `Attempts` | `int` | Total execution attempts made |
| `ErrorMessage` | `string?` | Last exception message |
| `CreatedAt` | `DateTime` | UTC time job was first enqueued |
| `DeadAt` | `DateTime` | UTC time job was moved to DLQ |

---

### Retry policies

| `BackoffType` | Delay formula | When to use |
|---------------|---------------|-------------|
| `Exponential` | `initialDelay × 2^(attempt−1)` | External APIs, rate-limited services |
| `Fixed` | `initialDelay` (constant) | Idempotent operations, fast retry acceptable |
| `Linear` | `initialDelay × attempt` | Moderate back-pressure |

```csharp
// Exponential: 500ms → 1s → 2s → 4s
q.Retry(maxAttempts: 5, BackoffType.Exponential, initialDelayMs: 500);

// Fixed: 1s → 1s → 1s
q.Retry(maxAttempts: 3, BackoffType.Fixed, initialDelayMs: 1000);

// Linear: 500ms → 1s → 1.5s → 2s
q.Retry(maxAttempts: 4, BackoffType.Linear, initialDelayMs: 500);
```

---

### Workers and concurrency

`Workers(count, concurrency)` controls the parallelism model for each queue.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `count` | `1` | Number of independent background worker threads started for this queue |
| `concurrency` | `5` | Maximum simultaneous jobs each worker processes at once |

Total parallel capacity = `count × concurrency`.

```csharp
builder.Services.AddJobQueue(options =>
    options.UseDefaultQueue(q =>
    {
        q.UseRedis(r => { r.Endpoint = "localhost:6379"; });

        // 3 workers × 10 concurrent jobs = up to 30 jobs running simultaneously
        q.Workers(count: 3, concurrency: 10);

        q.Retry(maxAttempts: 3, BackoffType.Exponential, initialDelayMs: 500);
    }));
```

**When to increase `count`:** jobs are I/O-bound and a single worker's polling loop becomes the bottleneck. Each worker maintains its own independent polling loop and semaphore.

**When to increase `concurrency` instead:** jobs are short-lived and you want more in-flight per worker without spinning up additional polling loops.

---

### Polling interval

`PollingInterval(TimeSpan)` controls how long each worker sleeps between polls when the queue is empty.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `interval` | `3s` | Sleep duration between polls when no jobs are found |

```csharp
builder.Services.AddJobQueue(options =>
    options.UseDefaultQueue(q =>
    {
        q.UseDatabaseDriver<AppDbContext>();
        q.Workers(concurrency: 4);

        // Poll every 500ms — lower latency for time-sensitive jobs
        q.PollingInterval(TimeSpan.FromMilliseconds(500));

        // Poll every 10s — fewer DB queries for low-traffic queues
        q.PollingInterval(TimeSpan.FromSeconds(10));
    }));
```

**When to decrease:** jobs are latency-sensitive and the 3s default is too slow to pick them up.

**When to increase:** the queue is low-traffic and you want to reduce backend load. At the default 3s, one worker with 5 concurrency slots generates ~1.7 queries/second at idle; at 10s that drops to ~0.5 queries/second.

> **Note:** `PollingInterval` has no effect on the in-memory driver. The memory driver blocks on a channel and wakes instantly when a job is enqueued — no polling occurs.

---

### Named queues

Use separate named queues to isolate workloads with different throughput or retry requirements.

```csharp
builder.Services.AddJobQueue(options =>
{
    options.UseQueue("emails", q =>
    {
        q.UseRedis(r =>
        {
            r.Endpoint = "localhost:6379";
            r.Password = "your-password";   // required in production
        });
        q.Workers(concurrency: 8);
        q.Retry(maxAttempts: 5, BackoffType.Exponential, initialDelayMs: 1000);
        q.EnableDeadLetterQueue();
    });

    options.UseQueue("reports", q =>
    {
        q.UseRedis(r =>
        {
            r.Endpoint = "localhost:6379";
            r.Password = "your-password";   // required in production
        });
        q.Workers(concurrency: 2); // limit concurrency for CPU-heavy jobs
        q.Retry(maxAttempts: 1, BackoffType.Fixed, initialDelayMs: 0);
    });
});

// Enqueue to a named queue
await queue.EnqueueAsync<SendEmailJob, EmailPayload>(
    "emails",
    new EmailPayload("user@example.com", "Hi", "Welcome!"));

await queue.EnqueueAsync<GenerateReportJob, ReportRequest>(
    "reports",
    new ReportRequest(year: 2025, month: 5));
```

---

### Other drivers

#### Redis Sentinel and Cluster

```csharp
// Sentinel — automatic failover
q.UseRedisSentinel(r =>
{
    r.ServiceName = "mymaster";
    r.Endpoints = new[] { "sentinel1:26379", "sentinel2:26379", "sentinel3:26379" };
    r.Password = "your-password";   // required in production
});

// Cluster — horizontal scale
q.UseRedisCluster(r =>
{
    r.Endpoints = new[] { "node1:7001", "node2:7002", "node3:7003" };
    r.Password = "your-password";   // required in production
});
```

#### EF Core database driver

Use this driver when jobs must persist durably in your application database. Multi-instance safe via optimistic locking.

**B.1 — Register `IDbContextFactory` and configure the driver**

```csharp
using DotInfraKit.Queue.Database;

builder.Services.AddDbContextFactory<AppDbContext>(opts =>
    opts.UseSqlServer(connectionString));

builder.Services.AddJobQueue(options =>
    options.UseDefaultQueue(q =>
    {
        q.UseDatabaseDriver<AppDbContext>();
        q.Workers(concurrency: 2);
        q.Retry(maxAttempts: 3, BackoffType.Exponential, initialDelayMs: 1000);
        q.EnableDeadLetterQueue();
    }));
```

> **Database polling overhead:** Each worker polls the database once per `PollingInterval` (default 3s) when the queue is empty. With `Workers(concurrency: 5)` that is ~1.7 queries/second at idle. For low-traffic queues, increase `PollingInterval` to reduce load:
>
> ```csharp
> q.UseDatabaseDriver<AppDbContext>();
> q.Workers(concurrency: 2);
> q.PollingInterval(TimeSpan.FromSeconds(10)); // 0.2 queries/second at idle
> ```

**B.2 — Add queue tables to your `DbContext`**

```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Your existing entity configurations...

        modelBuilder.AddDotInfraKitQueue(); // registers dotinfrakit_queue_jobs table
    }
}
```

Then apply the schema via EF migrations:

```
dotnet ef migrations add AddDotInfraKitQueue
dotnet ef database update
```

---

### Queue Monitoring

`DotInfraKit.Queue.Monitoring` exposes live queue state over HTTP — no external tooling required.

#### 1. Register and map in `Program.cs`

```csharp
using DotInfraKit.Queue.Monitoring;

// After AddJobQueue(...)
builder.Services.AddQueueMonitoring(opts =>
{
    opts.Path = "/queue-monitoring";  // default — all endpoints share this base path
    opts.BasicAuth = new BasicAuthOptions
    {
        Username = "admin",
        Password = "your-monitoring-password"
    };
});

// After builder.Build()
app.MapQueueMonitoring();
```

#### 2. Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `{path}/queues` | Paginated list of queue configurations |
| GET | `{path}/stats` | Live counts for every queue |
| GET | `{path}/queues/{name}/{id}` | Single job record by ID |

##### `GET {path}/queues`

| Query param | Default | Description |
|-------------|---------|-------------|
| `page` | `1` | Page number |
| `pageSize` | `20` | Items per page (max 100) |
| `name` | *(all)* | Filter to a specific queue name |

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 2,
  "data": [
    {
      "name": "default",
      "workerCount": 2,
      "concurrency": 5,
      "maxAttempts": 3,
      "backoffType": "Exponential",
      "dlqEnabled": true,
      "lockTimeout": "00:05:00",
      "delayedJobPollingInterval": "00:00:05",
      "pollingInterval": "00:00:03"
    }
  ]
}
```

##### `GET {path}/stats`

```json
{
  "timestamp": "2026-05-30T10:00:00.000Z",
  "queues": [
    { "name": "default", "pending": 42, "processing": 3, "delayed": 7, "deadLetter": 0 },
    { "name": "emails",  "pending": 15, "processing": 1, "delayed": 0, "deadLetter": 2 }
  ]
}
```

##### `GET {path}/queues/{name}/{id}`

Returns `404` when the queue name is unknown or the job ID is not found.

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "queueName": "default",
  "jobType": "MyApp.Jobs.SendEmailJob",
  "payload": "{\"to\":\"user@example.com\"}",
  "status": "processing",
  "attempts": 2,
  "maxAttempts": 3,
  "priority": 0,
  "nextRunAt": null,
  "lockedAt": "2026-05-30T10:00:00.000Z",
  "lockedBy": "server1:default:w0",
  "errorMessage": null,
  "createdAt": "2026-05-30T09:59:00.000Z",
  "completedAt": null
}
```

> **Memory driver note:** pending jobs inside the in-memory channel are not accessible by ID. Only `processing` and `delayed` jobs can be retrieved via the detail endpoint.

#### 3. Authorization options

**Built-in Basic Auth** (no ASP.NET Core auth pipeline required):

```csharp
opts.BasicAuth = new BasicAuthOptions { Username = "admin", Password = "secret" };
```

**ASP.NET Core policy-based auth** (opt-in, can be combined with Basic Auth):

```csharp
opts.RequireAuthorization = true;
opts.AuthorizationPolicy = "MonitoringPolicy"; // omit to use the default policy
```

---

## C. Cache

### 1. Register in `Program.cs`

```csharp
using DotInfraKit;
using DotInfraKit.Cache;

builder.Services.AddAppCache(c => c.UseRedis(r =>
{
    r.Endpoint = "localhost:6379";
    r.Password = "your-password";       // required in production
    r.KeyPrefix = "myapp:";             // prepended to every key automatically
}));
```

> **Other backends**
>
> ```csharp
> // Memory (single-instance / development)
> builder.Services.AddAppCache(c => c.UseMemory(m =>
>     m.DefaultExpiry = TimeSpan.FromMinutes(5)));
>
> // Redis Sentinel (high availability)
> builder.Services.AddAppCache(c => c.UseRedisSentinel(r =>
> {
>     r.ServiceName = "mymaster";
>     r.Endpoints = new[] { "sentinel1:26379", "sentinel2:26379", "sentinel3:26379" };
>     r.Password = "your-password";   // required in production
>     r.KeyPrefix = "myapp:";
> }));
>
> // Redis Cluster (horizontal scale)
> builder.Services.AddAppCache(c => c.UseRedisCluster(r =>
> {
>     r.Endpoints = new[] { "node1:7001", "node2:7002", "node3:7003" };
>     r.Password = "your-password";   // required in production
>     r.KeyPrefix = "myapp:";
> }));
> ```

### 2. Inject and use `ICacheService`

```csharp
using DotInfraKit.Cache;

public class ProductService
{
    private readonly IProductRepository _db;
    private readonly ICacheService _cache;

    public ProductService(IProductRepository db, ICacheService cache)
    {
        _db = db;
        _cache = cache;
    }

    // Cache-aside: returns cached value, or fetches from DB and caches the result
    public Task<Product?> GetByIdAsync(int id)
        => _cache.GetOrSetAsync(
            $"products:{id}",
            () => _db.FindAsync(id),
            expiry: TimeSpan.FromMinutes(15));

    public async Task UpdateAsync(Product product)
    {
        await _db.UpdateAsync(product);
        await _cache.ForgetAsync($"products:{product.Id}"); // invalidate single key
    }

    public async Task BulkUpdateAsync(IEnumerable<Product> products)
    {
        await _db.BulkUpdateAsync(products);
        await _cache.ForgetByPrefixAsync("products:");      // invalidate all product keys
    }
}
```

### 3. All available methods

| Method | Description |
|--------|-------------|
| `GetAsync<T>(key)` | Returns `null` if key is missing or expired |
| `SetAsync<T>(key, value, expiry?)` | Stores value; `expiry` overrides `DefaultExpiry` |
| `GetOrSetAsync<T>(key, factory, expiry?)` | Cache-aside — calls `factory` on miss, caches and returns the result |
| `ForgetAsync(key)` | Removes a single key |
| `ForgetByPrefixAsync(prefix)` | Removes all keys that start with `prefix` — works across all Redis Cluster nodes |
| `ExistsAsync(key)` | Returns `bool`; cheaper than `GetAsync` — no deserialization |

**Key naming tip:** Use `{entity}:{id}` patterns to group related keys so `ForgetByPrefixAsync` can invalidate them together:

```csharp
await cache.SetAsync($"users:{userId}", user);
await cache.SetAsync($"users:{userId}:settings", settings);

// Invalidate all keys for this user in one call
await cache.ForgetByPrefixAsync($"users:{userId}");
```

---

## License

[MIT](LICENSE)
