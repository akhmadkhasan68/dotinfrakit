<!-- markdownlint-disable MD024 -->
# Plugin Proposal: DotInfraKit

A lightweight, developer-friendly .NET 8+ plugin for background job scheduling, background job queuing, and caching. Inspired by [Coravel](https://github.com/jamesmh/coravel) and [BullMQ](https://docs.bullmq.io/), designed to be the batteries-included infrastructure toolkit for ASP.NET Core services — including microservice projects running multiple instances.

---

## Overview

| Module | Replaces | Inspired by |
| --- | --- | --- |
| `DotInfraKit.Scheduler` | Verbose Quartz.NET setup | Coravel Scheduler |
| `DotInfraKit.Queue` | `System.Threading.Channels` + manual workers | BullMQ (NestJS) |
| `DotInfraKit.Cache` | Raw `IDistributedCache` usage | Coravel Cache |

All three modules are independent — install only what you need.

---

## Microservice & Multi-instance Support

This plugin is designed to work correctly in microservice architectures where **each service runs multiple instances** (e.g. 2 replicas behind a load balancer).

The key rule is: **the driver you choose determines whether a module is instance-safe**.

### At a glance

| Module | Memory driver | Redis driver | Database driver |
| --- | --- | --- | --- |
| **Scheduler** | ❌ Duplicate job execution per instance | ✅ Cluster mode: one instance wins the lock | ✅ Cluster mode: one instance wins the lock |
| **Queue** | ❌ Isolated per instance, not shared | ✅ Shared queue, `locked_by` prevents duplicates | ✅ Shared queue, `locked_by` prevents duplicates |
| **Cache** | ❌ Each instance has its own local cache | ✅ Shared cache across all instances | — |

### Why Memory driver is NOT safe for multi-instance

```text
Instance A                    Instance B
    │                              │
    │ Queue "emails" (in-memory)   │ Queue "emails" (in-memory)
    │ [job1, job2, job3]           │ [job4, job5]
    │                              │
    ▼                              ▼
  Processes job1-3               Processes job4-5
  (never sees job4-5)            (never sees job1-3)

→ Enqueue on instance A is only processed by instance A.
  If instance A restarts, job1-3 are permanently lost.
```

Use Memory driver only in single-instance deployments or local development.

---

## Redis Topology Support

The plugin supports all three standard Redis deployment topologies through the same `StackExchange.Redis` connection layer.

### Single Node

One Redis instance. Simplest setup, no HA.

```csharp
cache.UseRedis(redis =>
{
    redis.Endpoint = "localhost:6379";
    redis.Password = "";
    redis.KeyPrefix = "myapp:";
});
```

### Redis Sentinel (High Availability)

Automatic failover with one primary and N replicas. The client follows the elected primary — no manual intervention needed on failover.

```csharp
cache.UseRedisSentinel(redis =>
{
    redis.ServiceName = "mymaster";
    redis.Endpoints = new[] { "sentinel1:26379", "sentinel2:26379", "sentinel3:26379" };
    redis.Password = "";
    redis.KeyPrefix = "myapp:";
});
```

### Redis Cluster (Horizontal Sharding)

Data is sharded across N master nodes using hash slots (0–16383). Provides both horizontal scaling and HA. Supported by all three modules.

```csharp
cache.UseRedisCluster(redis =>
{
    redis.Endpoints = new[]
    {
        "node1:6379",
        "node2:6379",
        "node3:6379",
    };
    redis.Password = "";
    redis.KeyPrefix = "myapp:";
});
```

The same `UseRedis` / `UseRedisSentinel` / `UseRedisCluster` methods are available on both `AddJobQueue` and `AddAppCache`.

### `ForgetByPrefixAsync` in Redis Cluster

In a Redis Cluster, different keys live on different nodes (different hash slots). A regular `SCAN` or `KEYS` command only queries one node. The plugin handles this by **scanning all master nodes in parallel**:

```text
Redis Cluster (3 masters)
│
├── node1 (slots 0–5460)    ← scanned for "myapp:users:*"
├── node2 (slots 5461–10922) ← scanned for "myapp:users:*"
└── node3 (slots 10923–16383) ← scanned for "myapp:users:*"
    │
    └── matched keys from ALL nodes collected → batch DEL
```

Implementation (internal to the plugin):

```csharp
var servers = _connection.GetServers();
var masterNodes = servers.Where(s => s.IsConnected && !s.IsReplica);

await Parallel.ForEachAsync(masterNodes, async (server, ct) =>
{
    await foreach (var key in server.KeysAsync(pattern: $"{_keyPrefix}{prefix}*"))
        await _db.KeyDeleteAsync(key);
});
```

This means `ForgetByPrefixAsync("users:")` correctly evicts matching keys across all cluster nodes — no stale data left behind.

### Topology comparison

| Topology | HA | Horizontal scale | `ForgetByPrefixAsync` |
| --- | --- | --- | --- |
| Single Node | ❌ | ❌ | Scans single node |
| Sentinel | ✅ Automatic failover | ❌ | Scans primary node |
| Cluster | ✅ HA per shard | ✅ Data sharded | Scans all master nodes |

---

## Package Structure

```text
DotInfraKit                  ← meta-package (installs all three)
DotInfraKit.Scheduler        ← job scheduling module
DotInfraKit.Queue            ← background job queue module
DotInfraKit.Cache            ← caching module
```

**Target framework**: .NET 8+
**Namespace**: `DotInfraKit`

---

## Module 1: DotInfraKit.Scheduler

### Problem

Quartz.NET is powerful but verbose. Registering a single cron job requires `JobKey`, `ITrigger`, `WithIdentity`, `AddJob`, `AddTrigger`, and separate scoped DI registration. With multiple instances this becomes a duplicate-execution problem unless Quartz clustering is configured — which adds even more boilerplate.

### Installation

```bash
dotnet add package DotInfraKit.Scheduler
```

Internal dependency: Quartz.NET (hidden behind the abstraction).

### Step 1 — Define a job

Implement `IScheduledJob`. Use constructor injection freely — each execution gets its own DI scope.

```csharp
using DotInfraKit.Scheduler;

public class CleanupNotificationsJob(
    AppDbContext db,
    ILogger<CleanupNotificationsJob> logger
) : IScheduledJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var deleted = await db.Notifications
            .Where(n => n.CreatedAt < DateTime.UtcNow.AddDays(-30))
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation("Deleted {Count} old notifications", deleted);
    }
}
```

### Step 2 — Register and configure

All jobs are configured in one fluent call. No additional `AddScoped<TJob>()` calls needed.

```csharp
// Program.cs / Startup.cs
builder.Services.AddJobScheduler(scheduler =>
{
    scheduler.Schedule<CleanupNotificationsJob>()
             .Monthly()
             .At(hour: 0, minute: 0);

    scheduler.Schedule<DailyDigestJob>()
             .Daily()
             .At(hour: 7, minute: 0);

    scheduler.Schedule<HourlyMetricsJob>()
             .Hourly();

    scheduler.Schedule<WeeklyReportJob>()
             .Weekly()
             .On(DayOfWeek.Monday)
             .At(hour: 6, minute: 30);

    // Raw cron expression
    scheduler.Schedule<CustomCronJob>()
             .WithCron("0 15 10 ? * MON-FRI");

    // Pull schedule from appsettings.json
    scheduler.Schedule<AuditJob>()
             .WithCronFromConfig("Jobs:Audit:Cron");
});
```

### Cluster mode (multi-instance)

Without cluster mode, every running instance executes every scheduled job — causing duplicate runs. Enable cluster mode to guarantee only **one instance fires each trigger**, even with N replicas.

```csharp
builder.Services.AddJobScheduler(scheduler =>
{
    scheduler.UseClusterMode(cluster =>
    {
        cluster.UseDatabaseStore(connectionString);
        cluster.InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
    });

    scheduler.Schedule<CleanupNotificationsJob>().Monthly().At(0, 0);
    scheduler.Schedule<DailyDigestJob>().Daily().At(7, 0);
});
```

Under the hood, `UseClusterMode` switches Quartz from `RAMJobStore` to `AdoJobStore`. The first instance to acquire the database row-level lock fires the job; all others skip it.

```text
Instance A                         Instance B
    │                                   │
    │◄──── Both watch same DB store ────►│
    │                                   │
    │  trigger fires at 00:00           │
    │                                   │
    ├── tries to lock row ──────────────►├── tries to lock row
    │   ACQUIRED ✓                      │   BLOCKED (waits)
    │                                   │
    ▼                                   │
 Executes CleanupJob               lock released, skipped
```

### Schedule API reference

| Method | Equivalent cron | Notes |
| --- | --- | --- |
| `.EveryMinute()` | `0 * * * * ?` | |
| `.EveryMinutes(n)` | `0 */n * * * ?` | |
| `.Hourly()` | `0 0 * * * ?` | |
| `.Daily()` | `0 0 0 * * ?` | Chain with `.At(h, m)` |
| `.Weekly()` | `0 0 0 ? * MON` | Chain with `.On(DayOfWeek)` + `.At(h, m)` |
| `.Monthly()` | `0 0 0 1 * ?` | Chain with `.At(h, m)` |
| `.WithCron(expr)` | raw | Standard Quartz cron |
| `.WithCronFromConfig(key)` | from `appsettings.json` | Enables runtime reconfiguration |

### Configuration

```json
"DotInfraKit": {
  "Scheduler": {
    "WaitForJobsToComplete": true
  }
},
"Jobs": {
  "Audit": {
    "Cron": "0 0 2 * * ?"
  }
}
```

### Interface contract

```csharp
namespace DotInfraKit.Scheduler;

public interface IScheduledJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
```

---

## Module 2: DotInfraKit.Queue

### Problem

ASP.NET Core's `IHostedService` + `System.Threading.Channels` pattern works for simple in-memory tasks but lacks persistence, retry logic, concurrency control, named queues, and multi-driver support. In multi-instance deployments the in-memory queue is completely isolated per instance — enqueued jobs may never be processed if the enqueuing instance is not the processing instance.

### Installation

```bash
dotnet add package DotInfraKit.Queue

# Optional driver packages
dotnet add package DotInfraKit.Queue.Redis
dotnet add package DotInfraKit.Queue.Database
```

### Key concepts

| Concept | Description |
| --- | --- |
| **Queue** | Named buffer of pending jobs |
| **Job** | A unit of work with a typed payload |
| **Worker** | Background process that picks up and executes jobs |
| **Driver** | Storage backend: Memory, Redis, or Database |
| **Concurrency** | Max number of jobs a single worker processes in parallel |
| **Retry Policy** | What to do when a job fails |
| **Dead-letter Queue (DLQ)** | Where permanently failed jobs go |

### Step 1 — Define a job

```csharp
using DotInfraKit.Queue;

public class SendWelcomeEmailJob(
    IEmailService emailService,
    ILogger<SendWelcomeEmailJob> logger
) : IQueueJob<WelcomeEmailPayload>
{
    public async Task ExecuteAsync(
        WelcomeEmailPayload payload,
        JobContext context,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Sending welcome email (attempt {Attempt}/{Max})",
            context.AttemptNumber, context.MaxAttempts);

        await emailService.SendWelcomeAsync(payload.Email, payload.Name);
    }
}

public record WelcomeEmailPayload(string Email, string Name);
```

### Step 2 — Configure queues

```csharp
builder.Services.AddJobQueue(options =>
{
    // Default queue — suitable for most use cases
    options.UseDefaultQueue(queue =>
    {
        queue.UseMemoryDriver(capacity: 200);   // ⚠ single-instance only
        queue.Workers(concurrency: 5);
        queue.Retry(maxAttempts: 3, BackoffType.Exponential, initialDelayMs: 2000);
    });

    // Multi-instance safe: Redis single node
    options.AddQueue("emails", queue =>
    {
        queue.UseRedis(r => r.Endpoint = "localhost:6379");
        queue.Workers(count: 2, concurrency: 10);
        queue.Retry(maxAttempts: 5, BackoffType.Exponential);
        queue.EnableDeadLetterQueue();
    });

    // Multi-instance safe: Redis Cluster
    options.AddQueue("notifications", queue =>
    {
        queue.UseRedisCluster(r => r.Endpoints = new[]
        {
            "node1:6379", "node2:6379", "node3:6379"
        });
        queue.Workers(count: 2, concurrency: 20);
        queue.Retry(maxAttempts: 3, BackoffType.Exponential);
        queue.EnableDeadLetterQueue();
    });

    // Durable queue backed by the application database (EF Core)
    options.AddQueue("reports", queue =>
    {
        queue.UseDatabaseDriver(db => db.UseDbContext<AppDbContext>());
        queue.Workers(count: 1, concurrency: 1);
        queue.Retry(maxAttempts: 3, BackoffType.Fixed, initialDelayMs: 5000);
        queue.EnableDeadLetterQueue();
    });
});
```

### Step 3 — Dispatch jobs

```csharp
public class UserService(IQueueService queue)
{
    public async Task CreateUserAsync(UserCreateDto dto)
    {
        var user = await _repo.CreateAsync(dto);

        // Enqueue to default queue
        await _queue.EnqueueAsync<SendWelcomeEmailJob, WelcomeEmailPayload>(
            new WelcomeEmailPayload(user.Email, user.Name)
        );

        // Enqueue to named queue
        await _queue.EnqueueAsync<GenerateOnboardingReportJob, ReportPayload>(
            "reports",
            new ReportPayload(user.Id)
        );

        // Enqueue with delay
        await _queue.EnqueueAsync<SendReminderEmailJob, ReminderPayload>(
            "emails",
            new ReminderPayload(user.Email),
            new EnqueueOptions { Delay = TimeSpan.FromHours(24) }
        );
    }
}
```

### Concurrency model

```text
Queue "emails" — UseRedis() — Workers(count: 2, concurrency: 10)
│
├── QueueWorkerService #1  (IHostedService)
│   └── SemaphoreSlim(10) → 10 concurrent ExecuteAsync calls
│
└── QueueWorkerService #2  (IHostedService)
    └── SemaphoreSlim(10) → 10 concurrent ExecuteAsync calls

Total per process: 20 simultaneous jobs, zero race conditions
```

Each worker loop:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await _semaphore.WaitAsync(stoppingToken);
    _ = Task.Run(async () =>
    {
        try   { await ProcessNextJobAsync(); }
        finally { _semaphore.Release(); }
    }, stoppingToken);
}
```

### Multi-instance safety (Redis / Database drivers)

With 2 service instances and a shared Redis or Database driver, every worker competes for the same jobs. The plugin prevents duplicate processing via **optimistic locking**:

```text
Instance A — worker          Instance B — worker
     │                               │
     │  SELECT next pending job      │  SELECT next pending job
     │                               │
     ├─ UPDATE locked_at = NOW()     │
     ├─ UPDATE locked_by = "A:w1"   │
     │  WHERE locked_at IS NULL      │  UPDATE ... WHERE locked_at IS NULL
     │  1 row affected ✓             │  0 rows affected ✗ (already locked)
     ▼                               │
  Processes job                  Skips, picks next pending job
```

`locked_by` format: `{machineId}:{workerId}` — unique per process per worker.

### Driver comparison

| Driver | Persistence | Multi-instance | Redis Cluster | Best for |
| --- | --- | --- | --- | --- |
| `MemoryDriver` | None | ❌ Isolated | — | Local dev, ephemeral tasks |
| `RedisDriver` (single node) | Yes | ✅ Shared, locked | — | High-throughput, simple infra |
| `RedisDriver` (cluster) | Yes | ✅ Shared, locked | ✅ | High-throughput, large scale |
| `DatabaseDriver` | Yes | ✅ Shared, locked | — | Auditable, no extra infra |

### Database driver — EF Core migration

The plugin ships a `QueueJobRecord` entity and a `ModelBuilder` extension. You register it in your existing `DbContext` and run a standard EF Core migration — no raw SQL needed, and the table lives in your normal migration history.

**Step 1 — Register the entity in your `DbContext`**

```csharp
public class AppDbContext : DbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddDotInfraKitQueue(); // registers QueueJobRecord + index
    }
}
```

**Step 2 — Run EF migration**

```bash
dotnet ef migrations add AddDotInfraKitQueue
dotnet ef database update
```

This generates a migration that creates `queue_jobs` alongside your other tables, fully version-controlled.

**Optional: auto-migrate on startup** (useful for dev/test environments)

```csharp
options.AddQueue("reports", queue =>
{
    queue.UseDatabaseDriver(db =>
    {
        db.UseDbContext<AppDbContext>();
        db.AutoMigrate(); // applies pending migrations on app start
    });
});
```

**`QueueJobRecord` entity** (public — query it directly if you need custom reporting)

```csharp
// Exposed from DotInfraKit.Queue
public class QueueJobRecord
{
    public Guid Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // pending|processing|completed|failed|dead
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }   // "{machineId}:{workerId}"
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

The `AddDotInfraKitQueue()` extension configures snake_case column names, default values, and the dequeue index internally — you do not need to configure any of that manually.

### Retry policy

```text
Attempt 1:  immediate
Attempt 2 (Exponential):  2^0 × 2000ms =  2 seconds
Attempt 3 (Exponential):  2^1 × 2000ms =  4 seconds
Attempt 4 (Exponential):  2^2 × 2000ms =  8 seconds

maxAttempts exceeded → status = "dead" (written to dead-letter queue)
```

Backoff types: `Exponential`, `Fixed`, `Linear`.

### Interface contracts

```csharp
namespace DotInfraKit.Queue;

public interface IQueueJob<TPayload>
{
    Task ExecuteAsync(TPayload payload, JobContext context, CancellationToken cancellationToken);
}

public interface IQueueService
{
    Task<Guid> EnqueueAsync<TJob, TPayload>(
        TPayload payload,
        EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>;

    Task<Guid> EnqueueAsync<TJob, TPayload>(
        string queueName,
        TPayload payload,
        EnqueueOptions? options = null)
        where TJob : IQueueJob<TPayload>;
}

public class JobContext
{
    public Guid JobId { get; init; }
    public string QueueName { get; init; } = string.Empty;
    public int AttemptNumber { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime EnqueuedAt { get; init; }
}

public class EnqueueOptions
{
    public int Priority { get; set; } = 0;
    public TimeSpan? Delay { get; set; }
    public DateTime? RunAt { get; set; }
}

public enum BackoffType { Exponential, Fixed, Linear }
```

### Configuration

```json
"DotInfraKit": {
  "Queue": {
    "Redis": {
      "Endpoint": "localhost:6379",
      "Password": ""
    },
    "RedisCluster": {
      "Endpoints": ["node1:6379", "node2:6379", "node3:6379"],
      "Password": ""
    }
  }
}
```

---

## Module 3: DotInfraKit.Cache

### Problem

Using `IDistributedCache` or `IMemoryCache` directly leads to repetitive get-or-set boilerplate, no cache strategy abstraction, and no selective invalidation. With multiple instances and a memory driver, each instance maintains its own local cache — a write on instance A never invalidates the stale cache on instance B.

### Installation

```bash
dotnet add package DotInfraKit.Cache
```

### Step 1 — Configure

```csharp
builder.Services.AddAppCache(cache =>
{
    // Single-instance or local dev
    cache.UseMemory();

    // OR single Redis node
    cache.UseRedis(redis =>
    {
        redis.Endpoint = "localhost:6379";
        redis.Password = "";
        redis.KeyPrefix = "myapp:";
        redis.PoolSize = 10;
    });

    // OR Redis Sentinel (HA failover)
    cache.UseRedisSentinel(redis =>
    {
        redis.ServiceName = "mymaster";
        redis.Endpoints = new[] { "sentinel1:26379", "sentinel2:26379", "sentinel3:26379" };
        redis.Password = "";
        redis.KeyPrefix = "myapp:";
    });

    // OR Redis Cluster (horizontal sharding + HA)
    cache.UseRedisCluster(redis =>
    {
        redis.Endpoints = new[] { "node1:6379", "node2:6379", "node3:6379" };
        redis.Password = "";
        redis.KeyPrefix = "myapp:";
    });

    cache.DefaultExpiry(TimeSpan.FromMinutes(30));

    // Auto-invalidate on HTTP mutations (POST, PUT, PATCH, DELETE)
    cache.EnableAutoInvalidation(prefix: "data:");
});
```

`EnableAutoInvalidation` registers a middleware that calls `ForgetByPrefixAsync` on every mutating request — no manual wiring needed.

### Step 2 — Use in services

```csharp
public class UserService(ICacheService cache, IUserRepository repo)
{
    public async Task<UserDto?> GetByIdAsync(Guid id)
        => await _cache.GetOrSetAsync(
            $"data:users:{id}",
            async () => await _repo.FindByIdAsync(id),
            TimeSpan.FromMinutes(10));

    public async Task UpdateAsync(Guid id, UserUpdateDto dto)
    {
        await _repo.UpdateAsync(id, dto);
        await _cache.ForgetAsync($"data:users:{id}");
        await _cache.ForgetByPrefixAsync("data:users:list:");
    }
}
```

### Multi-instance behavior

With Redis driver, the cache is shared. Any instance calling `ForgetAsync` or `ForgetByPrefixAsync` invalidates the key **for all instances** — no stale reads.

```text
Instance A                           Instance B
    │                                     │
    │ SetAsync("data:users:1", ...)        │
    │ ──────────────► Redis ◄─────────────│
    │                                     │
    │                             GetAsync("data:users:1")
    │                             ──► Redis ──► HIT ✓
    │                                     │
    │ ForgetAsync("data:users:1")          │
    │ ──────────────► Redis (DEL key)     │
    │                                     │
    │                             GetAsync("data:users:1")
    │                             ──► Redis ──► MISS → reloads from DB ✓
```

In Redis Cluster mode, `ForgetByPrefixAsync` scans all master nodes to ensure no matching key survives on any shard.

### Cache strategy

v1 supports **Cache-Aside** only. The cache service does not interact with the data store directly — the application code is responsible for reading from and writing to the source. `WriteThrough` is planned for v2 (see Roadmap).

| Strategy | Description | Version |
| --- | --- | --- |
| `CacheAside` | Check cache → miss → load → store → return | v1 ✅ |
| `WriteThrough` | Write to cache and source simultaneously | v2 (planned) |

### Driver comparison

| Driver | Shared across instances | Redis Cluster | Best for |
| --- | --- | --- | --- |
| `MemoryDriver` | ❌ Isolated per instance | — | Single-instance, local dev |
| `RedisDriver` (single) | ✅ Shared | ❌ | Multi-instance, simple infra |
| `RedisDriver` (sentinel) | ✅ Shared + HA | ❌ | Multi-instance, HA required |
| `RedisDriver` (cluster) | ✅ Shared + HA | ✅ | Large scale, data sharding |

### Interface contract

```csharp
namespace DotInfraKit.Cache;

public interface ICacheService
{
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null);
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);
    Task ForgetAsync(string key);
    Task ForgetByPrefixAsync(string prefix);
    Task<bool> ExistsAsync(string key);
}
```

### Configuration

```json
"DotInfraKit": {
  "Cache": {
    "Driver": "RedisCluster",
    "DefaultExpiryMinutes": 30,
    "KeyPrefix": "myapp:",
    "AutoInvalidation": {
      "Enabled": true,
      "Prefix": "data:"
    },
    "Redis": {
      "Endpoint": "localhost:6379",
      "Password": "",
      "PoolSize": 10
    },
    "RedisSentinel": {
      "ServiceName": "mymaster",
      "Endpoints": ["sentinel1:26379", "sentinel2:26379", "sentinel3:26379"],
      "Password": ""
    },
    "RedisCluster": {
      "Endpoints": ["node1:6379", "node2:6379", "node3:6379"],
      "Password": "",
      "PoolSize": 10
    }
  }
}
```

---

## Full Example: 2-instance Microservice with Redis Cluster

```csharp
// Program.cs — safe for N replicas of the same service
builder.Services
    .AddJobScheduler(scheduler =>
    {
        // Cluster mode: only one instance runs each job
        scheduler.UseClusterMode(cluster =>
        {
            cluster.UseDatabaseStore(connectionString);
            cluster.InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
        });

        scheduler.Schedule<CleanupExpiredTokensJob>().Daily().At(3, 0);
        scheduler.Schedule<GenerateSitemapJob>().Weekly().On(DayOfWeek.Sunday).At(1, 0);
    })
    .AddJobQueue(options =>
    {
        options.AddQueue("notifications", q =>
        {
            q.UseRedisCluster(r => r.Endpoints = new[]
            {
                "node1:6379", "node2:6379", "node3:6379"
            });
            q.Workers(count: 2, concurrency: 20);
            q.Retry(maxAttempts: 3, BackoffType.Exponential);
            q.EnableDeadLetterQueue();
        });
    })
    .AddAppCache(cache =>
    {
        cache.UseRedisCluster(redis =>
        {
            redis.Endpoints = new[] { "node1:6379", "node2:6379", "node3:6379" };
            redis.KeyPrefix = "user-service:";
        });
        cache.DefaultExpiry(TimeSpan.FromMinutes(30));
        cache.EnableAutoInvalidation(prefix: "data:");
    });
```

---

## Minimum Requirements

The plugin targets **.NET 8 and higher**, which includes ASP.NET Core 8, 9, 10, and all future versions. The .NET and ASP.NET Core version numbers are aligned — targeting .NET 8 means ASP.NET Core 8, targeting .NET 9 means ASP.NET Core 9, and so on.

| Requirement | Minimum version | Tested on |
| --- | --- | --- |
| .NET / ASP.NET Core | **8.0** | 8.0, 9.0, 10.0 |
| EF Core (`DatabaseDriver`) | **8.0** | 8.0, 9.0 |
| Quartz.NET (`Scheduler`) | **3.x** | 3.x |
| StackExchange.Redis (`RedisDriver`, `Cache`) | **2.x** | 2.x |

> **Note**: The plugin uses only stable, non-breaking APIs from the ASP.NET Core hosting model (`IHostedService`, `IServiceCollection`, `WebApplication`). It is compatible with both the minimal hosting model (`WebApplication.CreateBuilder`) and the classic `Startup`-based model.

### Internal dependencies

| Module | Dependency |
| --- | --- |
| `DotInfraKit.Scheduler` | Quartz.NET ≥ 3.x |
| `DotInfraKit.Queue.Redis` | StackExchange.Redis ≥ 2.x |
| `DotInfraKit.Queue.Database` | Microsoft.EntityFrameworkCore ≥ 8.x |
| `DotInfraKit.Cache` (Redis) | StackExchange.Redis ≥ 2.x |

---

## Testing

Automated tests are a hard requirement for every release of DotInfraKit. No module ships without passing unit and integration test suites. This section describes the test strategy, project structure, tooling, and the test helpers the plugin exposes for consumer applications.

### Test project structure

```text
DotInfraKit.sln
├── src/
│   ├── DotInfraKit.Scheduler/
│   ├── DotInfraKit.Queue/
│   ├── DotInfraKit.Queue.Redis/
│   ├── DotInfraKit.Queue.Database/
│   └── DotInfraKit.Cache/
│
└── tests/
    ├── DotInfraKit.Scheduler.Tests/       ← unit tests
    ├── DotInfraKit.Queue.Tests/           ← unit tests
    ├── DotInfraKit.Cache.Tests/           ← unit tests
    └── DotInfraKit.IntegrationTests/      ← integration tests (real Redis, real DB)
```

### Test tooling

| Package | Purpose |
| --- | --- |
| `xUnit` | Test framework |
| `FluentAssertions` | Readable assertions |
| `NSubstitute` | Mocking dependencies |
| `Microsoft.AspNetCore.Mvc.Testing` | In-process `WebApplicationFactory` for integration tests |
| `Testcontainers.Redis` | Spin up a real Redis instance per test run |
| `Testcontainers.MsSql` | Spin up a real SQL Server instance per test run |
| `Microsoft.EntityFrameworkCore.InMemory` | Fast in-memory DB for unit-level queue tests |

---

### Unit tests — Scheduler

Each `IScheduledJob` implementation must be testable in isolation without starting Quartz.NET.

```csharp
public class CleanupNotificationsJobTests
{
    [Fact]
    public async Task ExecuteAsync_DeletesNotificationsOlderThan30Days()
    {
        // Arrange
        var db = new AppDbContextBuilder()
            .WithNotifications(
                new Notification { CreatedAt = DateTime.UtcNow.AddDays(-31) }, // should be deleted
                new Notification { CreatedAt = DateTime.UtcNow.AddDays(-10) }  // should stay
            )
            .BuildInMemory();

        var job = new CleanupNotificationsJob(db, NullLoggerFactory.Instance);

        // Act
        await job.ExecuteAsync(CancellationToken.None);

        // Assert
        db.Notifications.Should().HaveCount(1);
        db.Notifications.Single().CreatedAt.Should().BeAfter(DateTime.UtcNow.AddDays(-30));
    }
}
```

**Fluent schedule API unit tests** — verify cron expressions produced by the builder:

```csharp
public class SchedulerBuilderTests
{
    [Theory]
    [InlineData("Monthly + At(0,0)",    "0 0 0 1 * ?")]
    [InlineData("Daily + At(7,0)",      "0 0 7 * * ?")]
    [InlineData("Weekly + Mon + At(6,30)", "0 30 6 ? * MON")]
    [InlineData("Hourly",               "0 0 * * * ?")]
    [InlineData("EveryMinutes(15)",     "0 */15 * * * ?")]
    public void Schedule_ProducesCorrectCronExpression(string description, string expectedCron)
    {
        var entry = description switch
        {
            "Monthly + At(0,0)"         => new ScheduleBuilder().Monthly().At(0, 0),
            "Daily + At(7,0)"           => new ScheduleBuilder().Daily().At(7, 0),
            "Weekly + Mon + At(6,30)"   => new ScheduleBuilder().Weekly().On(DayOfWeek.Monday).At(6, 30),
            "Hourly"                    => new ScheduleBuilder().Hourly(),
            "EveryMinutes(15)"          => new ScheduleBuilder().EveryMinutes(15),
            _ => throw new ArgumentOutOfRangeException()
        };

        entry.ToCronExpression().Should().Be(expectedCron);
    }
}
```

---

### Unit tests — Queue

Test job execution logic and retry policy in isolation, using an in-memory driver.

**Job execution:**

```csharp
public class SendWelcomeEmailJobTests
{
    [Fact]
    public async Task ExecuteAsync_CallsEmailService_WithCorrectPayload()
    {
        var emailService = Substitute.For<IEmailService>();
        var job = new SendWelcomeEmailJob(emailService, NullLogger<SendWelcomeEmailJob>.Instance);
        var payload = new WelcomeEmailPayload("user@example.com", "Alice");
        var context = new JobContext { AttemptNumber = 1, MaxAttempts = 3 };

        await job.ExecuteAsync(payload, context, CancellationToken.None);

        await emailService.Received(1).SendWelcomeAsync("user@example.com", "Alice");
    }

    [Fact]
    public async Task ExecuteAsync_WhenServiceThrows_ExceptionPropagates()
    {
        var emailService = Substitute.For<IEmailService>();
        emailService.SendWelcomeAsync(Arg.Any<string>(), Arg.Any<string>())
                    .ThrowsAsync(new SmtpException("connection refused"));

        var job = new SendWelcomeEmailJob(emailService, NullLogger<SendWelcomeEmailJob>.Instance);

        await job.Invoking(j => j.ExecuteAsync(
                new WelcomeEmailPayload("x@x.com", "X"),
                new JobContext(),
                CancellationToken.None))
            .Should().ThrowAsync<SmtpException>();
    }
}
```

**Retry policy:**

```csharp
public class RetryPolicyTests
{
    [Theory]
    [InlineData(BackoffType.Exponential, 2000, 1,  2000)]   // 2^0 × 2000
    [InlineData(BackoffType.Exponential, 2000, 2,  4000)]   // 2^1 × 2000
    [InlineData(BackoffType.Exponential, 2000, 3,  8000)]   // 2^2 × 2000
    [InlineData(BackoffType.Fixed,       5000, 1,  5000)]
    [InlineData(BackoffType.Fixed,       5000, 3,  5000)]
    [InlineData(BackoffType.Linear,      1000, 3,  3000)]   // attempt × delay
    public void CalculateDelay_ReturnsExpectedMilliseconds(
        BackoffType type, int initialMs, int attempt, int expectedMs)
    {
        var policy = new RetryPolicy { BackoffType = type, InitialDelayMs = initialMs };

        policy.CalculateDelay(attempt).TotalMilliseconds.Should().Be(expectedMs);
    }
}
```

---

### Unit tests — Cache

Test `ICacheService` behavior using the memory driver — no Redis required.

```csharp
public class CacheServiceTests
{
    private readonly ICacheService _cache;

    public CacheServiceTests()
    {
        var services = new ServiceCollection();
        services.AddAppCache(c => c.UseMemory());
        _cache = services.BuildServiceProvider().GetRequiredService<ICacheService>();
    }

    [Fact]
    public async Task GetOrSetAsync_CallsFactory_OnCacheMiss()
    {
        var callCount = 0;
        var result = await _cache.GetOrSetAsync("key1", async () =>
        {
            callCount++;
            return "value1";
        });

        result.Should().Be("value1");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrSetAsync_DoesNotCallFactory_OnCacheHit()
    {
        await _cache.SetAsync("key2", "cached");

        var callCount = 0;
        await _cache.GetOrSetAsync("key2", async () => { callCount++; return "new"; });

        callCount.Should().Be(0);
    }

    [Fact]
    public async Task ForgetAsync_RemovesKey()
    {
        await _cache.SetAsync("key3", "value");
        await _cache.ForgetAsync("key3");

        var result = await _cache.GetAsync<string>("key3");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ForgetByPrefixAsync_RemovesAllMatchingKeys()
    {
        await _cache.SetAsync("users:1", "Alice");
        await _cache.SetAsync("users:2", "Bob");
        await _cache.SetAsync("orders:1", "Order");

        await _cache.ForgetByPrefixAsync("users:");

        (await _cache.GetAsync<string>("users:1")).Should().BeNull();
        (await _cache.GetAsync<string>("users:2")).Should().BeNull();
        (await _cache.GetAsync<string>("orders:1")).Should().Be("Order");
    }
}
```

---

### Integration tests

Integration tests use **Testcontainers** to spin up real Redis and SQL Server instances in Docker. They run as part of CI and verify the full enqueue → worker → execute → retry lifecycle.

**Redis queue integration test:**

```csharp
public class RedisQueueIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().Build();

    public Task InitializeAsync() => _redis.StartAsync();
    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task EnqueueAsync_JobIsPickedUpAndExecuted()
    {
        var executed = new TaskCompletionSource<WelcomeEmailPayload>();

        await using var app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.ConfigureServices(services =>
            {
                services.AddJobQueue(options =>
                {
                    options.UseDefaultQueue(q =>
                    {
                        q.UseRedis(r => r.Endpoint = _redis.GetConnectionString());
                        q.Workers(concurrency: 1);
                    });
                });

                // Override email service to capture execution
                services.AddSingleton<IEmailService>(
                    new CaptureEmailService(executed));
            }));

        var queue = app.Services.GetRequiredService<IQueueService>();
        await queue.EnqueueAsync<SendWelcomeEmailJob, WelcomeEmailPayload>(
            new WelcomeEmailPayload("test@example.com", "Test"));

        var payload = await executed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        payload.Email.Should().Be("test@example.com");
    }

    [Fact]
    public async Task FailingJob_IsRetriedAndMovedToDlq()
    {
        // ... similar setup, job always throws, assert status = "dead" after maxAttempts
    }
}
```

**Database driver integration test:**

```csharp
public class DatabaseQueueIntegrationTests : IAsyncLifetime
{
    private readonly MsSqlContainer _db = new MsSqlBuilder().Build();

    [Fact]
    public async Task DatabaseDriver_PersistsJobAcrossRestart()
    {
        // Start app, enqueue job, stop app before worker picks it up
        // Restart app, assert job is still in queue and gets processed
    }

    [Fact]
    public async Task DatabaseDriver_TwoInstances_DoNotProcessSameJob()
    {
        // Start 2 app instances sharing the same DB
        // Enqueue 1 job
        // Assert it was executed exactly once across both instances
    }
}
```

**Redis Cluster integration test:**

```csharp
public class RedisClusterIntegrationTests : IAsyncLifetime
{
    // Uses a 3-node Redis Cluster via Testcontainers
    private readonly RedisClusterContainer _cluster = new RedisClusterBuilder()
        .WithNodeCount(3)
        .Build();

    [Fact]
    public async Task ForgetByPrefixAsync_EvictsKeysFromAllClusterNodes()
    {
        // Set keys that hash to different nodes
        await _cache.SetAsync("users:100", "Alice");   // → node1
        await _cache.SetAsync("users:200", "Bob");     // → node2
        await _cache.SetAsync("users:300", "Charlie"); // → node3

        await _cache.ForgetByPrefixAsync("users:");

        (await _cache.GetAsync<string>("users:100")).Should().BeNull();
        (await _cache.GetAsync<string>("users:200")).Should().BeNull();
        (await _cache.GetAsync<string>("users:300")).Should().BeNull();
    }
}
```

---

### Test helpers for consumer applications

The plugin ships a `DotInfraKit.Testing` package with fakes and helpers so that applications using DotInfraKit can write their own tests without running real Redis or databases.

```bash
dotnet add package DotInfraKit.Testing
```

**`FakeQueueService`** — records enqueued jobs, does not execute them:

```csharp
// In your app's unit tests — plain assertions (no FluentAssertions required)
var fakeQueue = new FakeQueueService();
var userService = new UserService(fakeQueue, repo);

await userService.CreateUserAsync(dto);

fakeQueue.AssertEnqueued<SendWelcomeEmailJob>();
fakeQueue.AssertEnqueued<SendWelcomeEmailJob, WelcomeEmailPayload>(
    p => p.Email == dto.Email);
```

**`FakeCacheService`** — in-memory cache with spy capabilities:

```csharp
var fakeCache = new FakeCacheService();
var userService = new UserService(fakeCache, repo);

await userService.GetByIdAsync(userId);
await userService.GetByIdAsync(userId); // second call

fakeCache.GetCallCount($"data:users:{userId}").Should().Be(2);
fakeCache.FactoryCallCount($"data:users:{userId}").Should().Be(1); // factory only called once
```

---

### CI pipeline requirements

Every pull request must pass all stages before merge:

```text
CI Pipeline
│
├── 1. Build          dotnet build --configuration Release
├── 2. Unit tests     dotnet test tests/DotInfraKit.*.Tests/
│                     (no external dependencies required)
│
├── 3. Integration    dotnet test tests/DotInfraKit.IntegrationTests/
│   tests             (requires Docker for Testcontainers)
│
├── 4. Coverage       minimum 80% line coverage per module
│                     (enforced via Coverlet threshold)
│
└── 5. Multi-version  matrix: net8.0 | net9.0 | net10.0
    build check
```

**Coverage threshold configuration** (in each test `.csproj`):

```xml
<PropertyGroup>
  <CollectCoverage>true</CollectCoverage>
  <CoverletOutputFormat>cobertura</CoverletOutputFormat>
  <Threshold>80</Threshold>
  <ThresholdType>line</ThresholdType>
  <ThresholdStat>total</ThresholdStat>
</PropertyGroup>
```

---

## Roadmap

| Feature | Priority | Notes |
| --- | --- | --- |
| Priority queues | High | Allow jobs to jump the line |
| Job deduplication | High | Skip enqueue if identical job is already pending |
| `WriteThrough` cache strategy | High | `SetAsync` accepting a `Func<T, Task> persistAction` delegate |
| Retry jitter | Medium | Prevent thundering herd on mass failures |
| Job progress reporting | Medium | `context.ReportProgressAsync(pct)` |
| Health check endpoints | Medium | `/health/scheduler`, `/health/queue` |
| Admin dashboard | Low | Job status, retry, DLQ viewer (Blazor or minimal API) |

---

## Design Decisions & Clarifications

This section addresses ambiguities that are not obvious from the API surface alone. Each note states the decision and the reason behind it.

---

### Queue: job class DI registration

**Decision — explicit, not auto-discovered.**

`AddJobQueue` does NOT scan assemblies for `IQueueJob<T>` implementations. The developer registers each job class manually:

```csharp
builder.Services.AddScoped<SendWelcomeEmailJob>();
```

If a job type is dequeued but not registered in DI, the plugin throws `InvalidOperationException` with a clear message at the point of execution, not silently. This is intentional — auto-discovery hides the dependency graph and makes DI lifetime bugs harder to find.

---

### Queue: job type resolution at runtime

**Decision — stored as `AssemblyQualifiedName`, resolved via DI scope.**

When a job is enqueued, `typeof(TJob).AssemblyQualifiedName` is stored as `job_type`. When a worker dequeues it, the plugin calls:

```csharp
var type = Type.GetType(jobRecord.JobType)
    ?? throw new InvalidOperationException($"Job type '{jobRecord.JobType}' could not be resolved.");

using var scope = _serviceScopeFactory.CreateScope();
var job = scope.ServiceProvider.GetRequiredService(type);
```

This means the job class must be registered in DI (see note above). The plugin creates a fresh DI scope per job execution — services injected into the job follow their registered lifetime within that scope.

---

### Queue: stuck/orphaned jobs (worker crash)

**Decision — `LockTimeout` with a background sweeper.**

Each queue configuration accepts a `LockTimeout` (default: 5 minutes):

```csharp
options.AddQueue("emails", queue =>
{
    queue.UseRedis();
    queue.LockTimeout(TimeSpan.FromMinutes(5)); // default
});
```

A `StuckJobSweeperService` background task runs every `LockTimeout / 2`. It queries jobs where `locked_at < UtcNow - LockTimeout` and resets their status to `pending`, increments `attempts`, and clears `locked_at`/`locked_by`. If `attempts >= max_attempts` after reset, the job moves to `dead` (DLQ) instead of re-queuing.

This applies to both `DatabaseDriver` and `RedisDriver`.

---

### Queue: `Workers(count:)` with Memory driver

**Decision — `count` is ignored for `MemoryDriver`; only one reader is created.**

`System.Threading.Channels` does support multiple concurrent readers, but with the Memory driver the channel itself is the queue — multiple readers would split jobs unpredictably across workers. To keep Memory driver behavior simple and predictable, `count` is capped at 1 and a warning is logged if the developer passes `count > 1`. Only `concurrency` (the `SemaphoreSlim` limit) is meaningful for the Memory driver.

---

### Scheduler: cluster mode database provider detection

**Decision — auto-detected from the registered EF Core `DbContext`; override available.**

`cluster.UseDatabaseStore(connectionString)` reads the provider name from the first registered `DbContext` via `IServiceProvider` at startup. If the provider is SQL Server, it uses `SqlServerDelegate`; PostgreSQL uses `PostgreSQLDelegate`, etc.

If no `DbContext` is registered or the provider cannot be determined, startup throws with a message asking the developer to specify explicitly:

```csharp
cluster.UseDatabaseStore(connectionString, QuartzDbProvider.SqlServer);
cluster.UseDatabaseStore(connectionString, QuartzDbProvider.PostgreSQL);
cluster.UseDatabaseStore(connectionString, QuartzDbProvider.MySQL);
```

---

### Cache: `WriteThrough` strategy — removed from v1

**Decision — `WriteThrough` is removed from the v1 interface.**

`WriteThrough` requires the cache service to know about the data source to write to both atomically. Without a data-store delegate parameter, the strategy would be misleading — calling `SetAsync` already writes to cache synchronously in every strategy.

v1 ships only `CacheAside`. `WriteThrough` is added to the roadmap with a proper interface design (`SetAsync` accepting an additional `Func<T, Task> persistAction`).

```csharp
// v1 — removed
public enum CacheStrategy { CacheAside, WriteThrough } // ← WriteThrough dropped

// v1 interface only has CacheAside behavior; no strategy enum needed
Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null);
```

---

### Cache: `ForgetByPrefixAsync` with Memory driver

**Decision — internal key registry via `ConcurrentDictionary`.**

`IMemoryCache` does not support key enumeration. When the Memory driver is active, the plugin maintains a `ConcurrentDictionary<string, byte>` as a key registry alongside the cache. Every `SetAsync` inserts the key; every `ForgetAsync` removes it. `ForgetByPrefixAsync` scans the registry with a prefix match and removes matching entries from both the registry and the cache.

This is O(n) over the number of tracked keys. It is acceptable for the Memory driver, which is intended for single-instance or dev use only.

---

### Queue: `EnqueueAsync` return type changed to `Task<Guid>`

**Decision — returns the job ID.**

`IQueueService.EnqueueAsync` is updated to return `Task<Guid>` instead of `Task`. The returned `Guid` is the `QueueJobRecord.Id`, allowing callers to log a correlation ID, poll job status, or pass the ID to a client for tracking.

```csharp
// Updated interface
Task<Guid> EnqueueAsync<TJob, TPayload>(TPayload payload, EnqueueOptions? options = null)
    where TJob : IQueueJob<TPayload>;

Task<Guid> EnqueueAsync<TJob, TPayload>(string queueName, TPayload payload, EnqueueOptions? options = null)
    where TJob : IQueueJob<TPayload>;
```

---

### Queue: `Priority` field — supported only by DatabaseDriver

**Decision — Priority is a `DatabaseDriver`-only feature. Ignored silently on other drivers.**

Redis `LIST` does not support per-item priority without switching to a sorted set (which would change the queue semantics). Rather than maintain two different Redis data structures conditionally, `Priority` is only honored by the `DatabaseDriver` (via `ORDER BY priority DESC, next_run_at ASC`).

When `Priority > 0` is used with `MemoryDriver` or `RedisDriver`, the plugin logs a one-time warning at startup:

```text
[WARN] DotInfraKit.Queue: Priority is set but the "emails" queue uses RedisDriver,
which does not support priority ordering. Jobs will be processed FIFO.
```

---

### Queue: delayed job polling interval

**Decision — a `DelayedJobPollingInterval` option; default 5 seconds.**

A `DelayedJobSweeperService` background task polls every `DelayedJobPollingInterval` (default: `TimeSpan.FromSeconds(5)`) for jobs where `next_run_at <= UtcNow AND status = 'pending'`. Those jobs are moved into the "ready" pool for workers to pick up in their normal dequeue loop.

```csharp
options.AddQueue("emails", queue =>
{
    queue.UseRedisDriver();
    queue.DelayedJobPollingInterval(TimeSpan.FromSeconds(5)); // default
});
```

For the `MemoryDriver`, delayed jobs are tracked in an in-memory sorted list by `RunAt` and are moved to the channel when ready.

---

### Queue: dead-letter queue API

**Decision — exposes `IDlqService` when `EnableDeadLetterQueue()` is called.**

`EnableDeadLetterQueue()` registers `IDlqService` in DI with the following operations:

```csharp
public interface IDlqService
{
    Task<IReadOnlyList<DlqJobRecord>> GetDeadJobsAsync(
        string queueName, int page = 1, int pageSize = 20);

    Task<Guid> RetryAsync(Guid jobId);       // re-enqueues job, resets attempts to 0
    Task RetryAllAsync(string queueName);    // retries all dead jobs in the queue
    Task DeleteAsync(Guid jobId);            // permanently removes from DLQ
    Task DeleteAllAsync(string queueName);   // clears all dead jobs in the queue
}
```

If `EnableDeadLetterQueue()` is not called, `IDlqService` is not registered and dead jobs are simply deleted.

---

### Scheduler: job failure behavior

**Decision — logs at `Error` level; optional `IScheduledJobExceptionHandler`.**

When `ExecuteAsync` throws, the plugin catches the exception, logs it at `Error` level with the job type name and full stack trace, and continues the scheduler loop. The job is not retried (retry is a Queue concern, not a Scheduler concern).

For Sentry or custom error tracking, the developer implements and registers `IScheduledJobExceptionHandler`:

```csharp
public interface IScheduledJobExceptionHandler
{
    Task HandleAsync(Type jobType, Exception exception, CancellationToken cancellationToken);
}
```

```csharp
// Registration
builder.Services.AddSingleton<IScheduledJobExceptionHandler, SentryJobExceptionHandler>();
```

If no handler is registered, the plugin falls back to logging only.

---

### Scheduler: `WithCronFromConfig` — missing key behavior

**Decision — throws `InvalidOperationException` at startup.**

If the config key does not exist when `AddJobScheduler` runs, the plugin throws immediately:

```text
InvalidOperationException: Cron expression for job 'AuditJob' could not be found
at config key 'Jobs:Audit:Cron'. Ensure the key exists in appsettings.json.
```

This is a fail-fast design — a silently skipped job in production is worse than a startup crash that surfaces the misconfiguration immediately.

---

### General: `using` directives

**Decision — one `using` for registration, one per module for interfaces.**

Extension methods (`AddJobScheduler`, `AddJobQueue`, `AddAppCache`) are in the `DotInfraKit` root namespace. One directive covers all registrations:

```csharp
using DotInfraKit;
```

Job/service interfaces require their module namespace:

```csharp
using DotInfraKit.Scheduler;  // IScheduledJob
using DotInfraKit.Queue;      // IQueueJob<T>, IQueueService, IDlqService
using DotInfraKit.Cache;      // ICacheService
```

---

### Scheduler: `InstanceId` default

**Decision — auto-generated; override recommended in production.**

Default: `$"{Environment.MachineName}-{Guid.NewGuid():N}"` — generated once at process startup. This is unique per process restart, which is correct for most cases.

In containerised environments, `Environment.MachineName` is the pod/container name, making the default already meaningful. Explicit override is recommended when deterministic naming is needed (e.g. for log correlation):

```csharp
cluster.InstanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
```

---

### Queue: `KeyPrefix` for Redis driver

**Decision — supported; queue keys follow a namespaced pattern.**

`UseRedis` and `UseRedisCluster` on the queue builder both support `KeyPrefix`:

```csharp
queue.UseRedis(r =>
{
    r.Endpoint = "localhost:6379";
    r.KeyPrefix = "user-service:";   // optional, default is empty
});
```

Queue keys are stored as `{KeyPrefix}queue:{queueName}`. Multiple services sharing one Redis instance should always set a unique `KeyPrefix` to avoid key collisions.

---

### Testing: Redis Cluster in integration tests

**Decision — Docker Compose cluster for CI; single-node fallback for local.**

`Testcontainers.Redis` does not currently provide a Redis Cluster container out of the box. The integration test suite uses a Docker Compose file (`docker-compose.test.yml`) that spins up a 3-node Redis Cluster for CI environments.

Locally, tests that require Redis Cluster can be skipped via a trait:

```csharp
[Fact]
[Trait("Category", "RedisCluster")]
public async Task ForgetByPrefixAsync_EvictsKeysFromAllClusterNodes() { ... }
```

Run cluster tests explicitly with: `dotnet test --filter "Category=RedisCluster"`.

---

### Testing: `AutoMigrate()` production guard

**Decision — throws if called in the Production environment.**

`AutoMigrate()` checks `IWebHostEnvironment.IsProduction()` at startup. If the app is running in production, it throws:

```
InvalidOperationException: AutoMigrate() is not allowed in the Production environment.
Run 'dotnet ef database update' as part of your deployment pipeline, or pass
AutoMigrate(allowInProduction: true) to explicitly override this guard.
```

The override escape hatch exists for teams that deliberately run migrations on startup (e.g., in CD pipelines where the app itself is the migration runner):

```csharp
db.AutoMigrate(allowInProduction: true);
```

---

### Testing: `DotInfraKit.Testing` assertion API and FluentAssertions dependency

**Decision — plain helpers in the base package; FluentAssertions extensions in a separate package.**

`DotInfraKit.Testing` ships plain assertion helpers that work with any test framework:

```csharp
fakeQueue.AssertEnqueued<SendWelcomeEmailJob>();
fakeQueue.AssertEnqueued<SendWelcomeEmailJob, WelcomeEmailPayload>(p => p.Email == "x@x.com");
fakeQueue.AssertNotEnqueued<SendWelcomeEmailJob>();
fakeQueue.AssertEnqueuedCount<SendWelcomeEmailJob>(2);
```

Teams using FluentAssertions install the optional extension package:

```bash
dotnet add package DotInfraKit.Testing.FluentAssertions
```

Which enables the fluent syntax:

```csharp
fakeQueue.Should().HaveEnqueued<SendWelcomeEmailJob>()
         .WithPayload<WelcomeEmailPayload>(p => p.Email == "x@x.com");
```

FluentAssertions is not a dependency of the base `DotInfraKit.Testing` package.

---

### Queue: `IDlqService` with `RedisDriver` — storage model

**Decision — Redis driver stores dead jobs in a separate Redis Hash; `IDlqService` has driver-specific implementations.**

`QueueJobRecord` is an EF Core entity and only exists in the `DatabaseDriver` context. For `RedisDriver`, dead jobs are stored in a Redis Hash keyed by job ID:

```text
{KeyPrefix}dlq:{queueName}  →  Hash
    {jobId}  →  JSON-serialized DlqJobRecord
```

`DlqJobRecord` is a plain C# class (not an EF entity) shared across both drivers:

```csharp
public class DlqJobRecord
{
    public Guid Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime DeadAt { get; set; }
}
```

The plugin registers the correct `IDlqService` implementation automatically based on the configured driver:

| Driver | `IDlqService` implementation |
| --- | --- |
| `MemoryDriver` | `InMemoryDlqService` — `ConcurrentDictionary` |
| `RedisDriver` | `RedisDlqService` — Redis Hash |
| `DatabaseDriver` | `DatabaseDlqService` — EF Core `QueueJobRecord` |

The `IDlqService` interface is the same regardless of driver — callers do not need to know which implementation is active.

---

### Queue: `StuckJobSweeperService` locking model for `RedisDriver`

**Decision — job metadata stored in a Redis Hash alongside the queue list.**

For `DatabaseDriver`, `locked_at` and `locked_by` are columns on the `queue_jobs` table — the sweeper runs a SQL query.

For `RedisDriver`, each job has a companion metadata Hash:

```text
{KeyPrefix}meta:{queueName}:{jobId}  →  Hash
    locked_at   →  ISO-8601 timestamp (or absent if unlocked)
    locked_by   →  "{machineId}:{workerId}" (or absent)
    attempts    →  integer
    status      →  "pending" | "processing" | "failed" | "dead"
```

The `StuckJobSweeperService` scans keys matching `{KeyPrefix}meta:{queueName}:*`, reads `locked_at` from each hash, and for any job where `locked_at` is older than `LockTimeout`:

1. Increments `attempts`
2. Clears `locked_at` and `locked_by`
3. If `attempts >= max_attempts` → moves job to DLQ Hash, sets `status = "dead"`
4. Otherwise → sets `status = "pending"`, re-pushes the job payload to the queue `LIST`

The payload itself remains in a separate Redis String key (`{KeyPrefix}payload:{jobId}`) and is only deleted on completion or DLQ promotion.

---

### Queue: behavior when `EnableDeadLetterQueue()` is not called

**Decision — permanently deleted from the store with no record kept.**

When a job exceeds `maxAttempts` and `EnableDeadLetterQueue()` was not configured:

- `DatabaseDriver`: the `queue_jobs` row is hard-deleted
- `RedisDriver`: the payload key and metadata hash are both deleted
- `MemoryDriver`: the in-memory entry is discarded

No trace is kept. If audit trails of failed jobs are required, `EnableDeadLetterQueue()` must always be called. The plugin logs a single `Warning` at the moment a job is discarded:

```text
[WARN] DotInfraKit.Queue: Job {JobId} ({JobType}) exceeded max attempts and was
permanently deleted. Enable dead-letter queue to retain failed jobs.
```
