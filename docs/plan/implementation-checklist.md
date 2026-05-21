# DotInfraKit — Implementation Checklist

> Derived from: `docs/wiki/plugin-proposal-dotinfrakit.md`
> Target: .NET 8+, ASP.NET Core, multi-instance safe

---

## Phase 1: Project Scaffold ✅

- [x] Create solution file `DotInfraKit.sln`
- [x] Create `Directory.Build.props` with shared properties (TargetFramework: `net8.0`, Nullable, ImplicitUsings, TreatWarningsAsErrors)
- [x] Create `Directory.Packages.props` for centralized NuGet version management
- [x] Scaffold `src/DotInfraKit.Scheduler/` project
- [x] Scaffold `src/DotInfraKit.Queue/` project
- [x] Scaffold `src/DotInfraKit.Queue.Redis/` project
- [x] Scaffold `src/DotInfraKit.Queue.Database/` project
- [x] Scaffold `src/DotInfraKit.Cache/` project
- [x] Scaffold `src/DotInfraKit/` meta-package project (references all five src modules)
- [x] Scaffold `src/DotInfraKit.Testing/` project
- [x] Scaffold `src/DotInfraKit.Testing.FluentAssertions/` project
- [x] Scaffold `tests/DotInfraKit.Scheduler.Tests/` project
- [x] Scaffold `tests/DotInfraKit.Queue.Tests/` project
- [x] Scaffold `tests/DotInfraKit.Cache.Tests/` project
- [x] Scaffold `tests/DotInfraKit.IntegrationTests/` project
- [x] Add all projects to solution
- [x] Configure NuGet package metadata per project (PackageId, Description, Tags, License, RepositoryUrl)
- [x] Add `.editorconfig` and `.gitignore`
- [x] Add `nuget.config` (clears stale global local source, uses nuget.org only)

---

## Phase 2: DotInfraKit.Scheduler ✅

### Interfaces & Models

- [x] Define `IScheduledJob` interface (`Task ExecuteAsync(CancellationToken)`)
- [x] Define `IScheduledJobExceptionHandler` interface (`Task HandleAsync(Type, Exception, CancellationToken)`)

### Schedule Builder (Fluent API)

- [x] Create `ScheduleBuilder` class
- [x] Implement `.EveryMinute()` → cron `0 * * * * ?`
- [x] Implement `.EveryMinutes(n)` → cron `0 */n * * * ?`
- [x] Implement `.Hourly()` → cron `0 0 * * * ?`
- [x] Implement `.Daily()` → cron `0 0 0 * * ?`
- [x] Implement `.Weekly()` → cron `0 0 0 ? * MON`
- [x] Implement `.Monthly()` → cron `0 0 0 1 * ?`
- [x] Implement `.At(hour, minute)` — chain modifier for Daily/Weekly/Monthly
- [x] Implement `.On(DayOfWeek)` — chain modifier for Weekly
- [x] Implement `.WithCron(expr)` — raw Quartz cron expression
- [x] Implement `.WithCronFromConfig(configKey)` — reads from `IConfiguration`; throws `InvalidOperationException` at startup if key missing
- [x] Implement `.ToCronExpression()` — produces final cron string

### Scheduler Configurator

- [x] Create `JobSchedulerBuilder` class
- [x] Implement `.Schedule<TJob>()` — returns `ScheduleBuilder` for chaining
- [x] Implement `.UseClusterMode(Action<ClusterOptions>)` — switches Quartz to `AdoJobStore`

### Cluster Mode

- [x] Create `ClusterOptions` with `InstanceId` and `UseDatabaseStore(connectionString)` / `UseDatabaseStore(connectionString, QuartzDbProvider)`
- [x] Auto-detect EF Core provider via reflection (no hard EFCore dep in Scheduler)
- [x] Throw descriptive `InvalidOperationException` if provider cannot be auto-detected
- [x] Expose `QuartzDbProvider` enum (AutoDetect, SqlServer, PostgreSQL, MySQL)
- [x] Default `InstanceId` to `$"{Environment.MachineName}-{Guid.NewGuid():N}"`

### DI Registration

- [x] Implement `AddJobScheduler(IServiceCollection, Action<JobSchedulerBuilder>)` extension in `DotInfraKit` namespace
- [x] Register Quartz.NET with RAMJobStore (default) or AdoJobStore (cluster mode via `UsePersistentStore`)
- [x] Read `DotInfraKit:Scheduler:WaitForJobsToComplete` from config
- [x] Each `IScheduledJob` resolves from Quartz's DI-managed scope per execution

### Error Handling

- [x] Catch exceptions from `ExecuteAsync`, log at `Error` level with job type name
- [x] Invoke `IScheduledJobExceptionHandler` if registered; silently continue otherwise

### Unit Tests (22 passing)

- [x] `ScheduleBuilderTests` — all schedule methods + `WithCronFromConfig` + missing key throws
- [x] `ScheduledJobRunnerTests` — execute called, exception caught, handler invoked, bad type throws

---

## Phase 3: DotInfraKit.Queue (Core)

### Interfaces & Models

- [ ] Define `IQueueJob<TPayload>` interface (`Task ExecuteAsync(TPayload, JobContext, CancellationToken)`)
- [ ] Define `IQueueService` interface with two `EnqueueAsync<TJob, TPayload>` overloads returning `Task<Guid>`
- [ ] Define `IDlqService` interface (`GetDeadJobsAsync`, `RetryAsync`, `RetryAllAsync`, `DeleteAsync`, `DeleteAllAsync`)
- [ ] Define `JobContext` record/class (`JobId`, `QueueName`, `AttemptNumber`, `MaxAttempts`, `EnqueuedAt`)
- [ ] Define `EnqueueOptions` class (`Priority`, `Delay`, `RunAt`)
- [ ] Define `BackoffType` enum (`Exponential`, `Fixed`, `Linear`)
- [ ] Define `DlqJobRecord` POCO (`Id`, `QueueName`, `JobType`, `Payload`, `Attempts`, `ErrorMessage`, `CreatedAt`, `DeadAt`)

### Retry Policy

- [ ] Implement `RetryPolicy` with `CalculateDelay(attempt)`:
  - `Exponential`: `2^(attempt-1) × initialDelayMs`
  - `Fixed`: `initialDelayMs` always
  - `Linear`: `attempt × initialDelayMs`

### Memory Driver

- [ ] Implement `MemoryQueueDriver` using `System.Threading.Channels`
- [ ] Cap `Workers(count)` at 1 for Memory driver; log warning if `count > 1`
- [ ] Track delayed jobs in in-memory sorted list by `RunAt`; move to channel when ready

### Worker Service

- [ ] Implement `QueueWorkerService : IHostedService`
- [ ] Use `SemaphoreSlim(concurrency)` for parallel job execution
- [ ] Worker loop: dequeue → acquire semaphore → `Task.Run(ProcessNextJobAsync)` → release semaphore
- [ ] Support multiple worker instances (`count`) per queue
- [ ] Resolve job type via `Type.GetType(jobRecord.JobType)` + `IServiceScopeFactory.CreateScope()`
- [ ] Throw `InvalidOperationException` with clear message if job type not found in DI

### Stuck Job Sweeper

- [ ] Implement `StuckJobSweeperService : IHostedService`
- [ ] Default `LockTimeout`: 5 minutes; polling interval: `LockTimeout / 2`
- [ ] On timeout: increment `attempts`, clear `locked_at`/`locked_by`
- [ ] If `attempts >= max_attempts` after reset → move to DLQ (or delete if DLQ not enabled)
- [ ] Log `Warning` when job permanently deleted (no DLQ)

### Delayed Job Sweeper

- [ ] Implement `DelayedJobSweeperService : IHostedService`
- [ ] Default polling interval: 5 seconds
- [ ] Query `next_run_at <= UtcNow AND status = 'pending'` → move to ready pool

### Dead-Letter Queue

- [ ] Implement `InMemoryDlqService` using `ConcurrentDictionary`
- [ ] Register `IDlqService` only when `EnableDeadLetterQueue()` is called

### DI Registration

- [ ] Implement `AddJobQueue(IServiceCollection, Action<QueueOptions>)` extension in `DotInfraKit` namespace
- [ ] Implement `UseDefaultQueue(Action<QueueBuilder>)` and `AddQueue(name, Action<QueueBuilder>)` on `QueueOptions`
- [ ] Implement `QueueBuilder` methods: `UseMemoryDriver`, `Workers(count, concurrency)`, `Retry(maxAttempts, BackoffType, initialDelayMs)`, `EnableDeadLetterQueue()`, `LockTimeout(TimeSpan)`, `DelayedJobPollingInterval(TimeSpan)`
- [ ] Log one-time startup warning when `Priority > 0` used with Memory or Redis driver

---

## Phase 4: DotInfraKit.Queue.Redis

### Redis Connection

- [ ] Implement `UseRedis(Action<RedisOptions>)` on `QueueBuilder` (single node)
- [ ] Implement `UseRedisSentinel(Action<RedisSentinelOptions>)` on `QueueBuilder`
- [ ] Implement `UseRedisCluster(Action<RedisClusterOptions>)` on `QueueBuilder`
- [ ] Support `KeyPrefix` on all Redis option types; queue keys follow `{KeyPrefix}queue:{queueName}`

### Redis Driver

- [ ] Implement `RedisQueueDriver` backed by Redis `LIST` (RPUSH enqueue, BLPOP dequeue)
- [ ] Store job payload in `{KeyPrefix}payload:{jobId}` (Redis String)
- [ ] Store metadata in `{KeyPrefix}meta:{queueName}:{jobId}` (Redis Hash: `locked_at`, `locked_by`, `attempts`, `status`)
- [ ] Implement optimistic locking: `UPDATE ... WHERE locked_at IS NULL` equivalent via Redis Hash conditional set
- [ ] `locked_by` format: `{machineId}:{workerId}`

### Redis DLQ

- [ ] Implement `RedisDlqService`
- [ ] Dead jobs stored in `{KeyPrefix}dlq:{queueName}` Redis Hash (`{jobId}` → JSON `DlqJobRecord`)
- [ ] Implement all `IDlqService` operations against Redis Hash

### Stuck Job Sweeper (Redis)

- [ ] Scan `{KeyPrefix}meta:{queueName}:*` keys, read `locked_at` from each Hash
- [ ] On timeout: increment `attempts`, clear `locked_at`/`locked_by`, re-push payload to LIST or move to DLQ

---

## Phase 5: DotInfraKit.Queue.Database

### Entity & Migration

- [ ] Define `QueueJobRecord` entity with all columns: `Id`, `QueueName`, `JobType`, `Payload`, `Status`, `Attempts`, `MaxAttempts`, `NextRunAt`, `LockedAt`, `LockedBy`, `ErrorMessage`, `CreatedAt`, `CompletedAt`
- [ ] Implement `AddDotInfraKitQueue(ModelBuilder)` extension: snake_case column names, default values, dequeue index
- [ ] Status values: `pending | processing | completed | failed | dead`

### Database Driver

- [ ] Implement `DatabaseQueueDriver` using EF Core + optimistic locking (`WHERE locked_at IS NULL` → 1 row affected)
- [ ] Dequeue query: `ORDER BY priority DESC, next_run_at ASC` (priority only honored here)
- [ ] Implement `UseDatabaseDriver(Action<DatabaseDriverOptions>)` on `QueueBuilder`
- [ ] Support `UseDbContext<TContext>()` on `DatabaseDriverOptions`

### AutoMigrate

- [ ] Implement `AutoMigrate(bool allowInProduction = false)` on `DatabaseDriverOptions`
- [ ] Check `IWebHostEnvironment.IsProduction()` at startup; throw `InvalidOperationException` if production and `allowInProduction` is false

### Database DLQ

- [ ] Implement `DatabaseDlqService` querying `QueueJobRecord` where `status = 'dead'`
- [ ] `RetryAsync`: reset `status = 'pending'`, `attempts = 0`, clear `locked_at`/`locked_by`

---

## Phase 6: DotInfraKit.Cache

### Interface

- [ ] Define `ICacheService` interface: `GetOrSetAsync<T>`, `GetAsync<T>`, `SetAsync<T>`, `ForgetAsync`, `ForgetByPrefixAsync`, `ExistsAsync`

### Memory Driver

- [ ] Implement `MemoryCacheDriver` wrapping `IMemoryCache`
- [ ] Maintain `ConcurrentDictionary<string, byte>` key registry alongside cache
- [ ] `SetAsync` → insert key into registry
- [ ] `ForgetAsync` → remove from registry + cache
- [ ] `ForgetByPrefixAsync` → prefix-scan registry (O(n)), delete matching keys from both registry and cache

### Redis Driver (Single Node)

- [ ] Implement `RedisCacheDriver` using `StackExchange.Redis`
- [ ] Support `KeyPrefix`, `PoolSize`
- [ ] `ForgetByPrefixAsync` → `SCAN` on single node + batch `DEL`

### Redis Sentinel Driver

- [ ] Implement `RedisSentinelCacheDriver`
- [ ] Support `ServiceName`, `Endpoints[]`, `Password`, `KeyPrefix`
- [ ] `ForgetByPrefixAsync` → `SCAN` primary node + batch `DEL`

### Redis Cluster Driver

- [ ] Implement `RedisClusterCacheDriver`
- [ ] `ForgetByPrefixAsync` → scan **all master nodes** in parallel (`Parallel.ForEachAsync`); collect matched keys → batch `DEL`

### Auto-Invalidation Middleware

- [ ] Implement `CacheAutoInvalidationMiddleware`
- [ ] On HTTP `POST`, `PUT`, `PATCH`, `DELETE` → call `ForgetByPrefixAsync(prefix)` after response
- [ ] Register via `EnableAutoInvalidation(prefix)` on cache builder

### DI Registration

- [ ] Implement `AddAppCache(IServiceCollection, Action<CacheBuilder>)` extension in `DotInfraKit` namespace
- [ ] Implement `CacheBuilder` methods: `UseMemory()`, `UseRedis(Action<RedisOptions>)`, `UseRedisSentinel(Action<RedisSentinelOptions>)`, `UseRedisCluster(Action<RedisClusterOptions>)`, `DefaultExpiry(TimeSpan)`, `EnableAutoInvalidation(prefix)`
- [ ] Read config from `DotInfraKit:Cache` section

---

## Phase 7: DotInfraKit.Testing

- [ ] Implement `FakeQueueService : IQueueService`
  - Records enqueued jobs in-memory, does NOT execute them
  - `AssertEnqueued<TJob>()`
  - `AssertEnqueued<TJob, TPayload>(Func<TPayload, bool> predicate)`
  - `AssertNotEnqueued<TJob>()`
  - `AssertEnqueuedCount<TJob>(int expected)`
- [ ] Implement `FakeCacheService : ICacheService`
  - In-memory cache with spy capabilities
  - `GetCallCount(key)` — times `GetAsync` / `GetOrSetAsync` called for key
  - `FactoryCallCount(key)` — times factory was actually invoked (cache miss count)

---

## Phase 8: DotInfraKit.Testing.FluentAssertions

- [ ] Add `FluentAssertions` NuGet dependency (not in base Testing package)
- [ ] Implement `FakeQueueServiceAssertions` with fluent syntax:
  - `fakeQueue.Should().HaveEnqueued<TJob>()`
  - `.WithPayload<TPayload>(Func<TPayload, bool> predicate)`
- [ ] Implement `FakeCacheServiceAssertions` with fluent syntax

---

## Phase 9: Unit Tests

### DotInfraKit.Scheduler.Tests

- [ ] Add xUnit, FluentAssertions, NSubstitute, Microsoft.Extensions.Logging.Abstractions
- [ ] Test `ScheduleBuilder.ToCronExpression()` for all schedule methods (Theory/InlineData)
- [ ] Test each `IScheduledJob` implementation in isolation (no Quartz.NET required)
- [ ] Test `WithCronFromConfig` throws on missing key
- [ ] Test `IScheduledJobExceptionHandler` is invoked on job failure

### DotInfraKit.Queue.Tests

- [ ] Add xUnit, FluentAssertions, NSubstitute, EFCore.InMemory
- [ ] Test job `ExecuteAsync` called with correct payload
- [ ] Test exception propagation from job
- [ ] Test `RetryPolicy.CalculateDelay` for all `BackoffType` values (Theory/InlineData)
- [ ] Test Memory driver: enqueue → dequeue order (FIFO)
- [ ] Test `Workers(count > 1)` logs warning with Memory driver
- [ ] Test `Priority > 0` with Memory/Redis driver logs warning

### DotInfraKit.Cache.Tests

- [ ] Add xUnit, FluentAssertions
- [ ] Test `GetOrSetAsync` calls factory on miss, skips on hit
- [ ] Test `ForgetAsync` removes key
- [ ] Test `ForgetByPrefixAsync` removes only matching keys (Memory driver)
- [ ] Test `ExistsAsync` returns correct bool
- [ ] Test `DefaultExpiry` applied when no expiry passed

---

## Phase 10: Integration Tests

- [ ] Add Testcontainers.Redis, Testcontainers.MsSql, Microsoft.AspNetCore.Mvc.Testing
- [ ] Create `docker-compose.test.yml` for 3-node Redis Cluster (CI)
- [ ] Mark Redis Cluster tests with `[Trait("Category", "RedisCluster")]`

### Redis Queue

- [ ] `EnqueueAsync` → job picked up and executed by worker (TaskCompletionSource pattern)
- [ ] Failing job retried and moved to DLQ after `maxAttempts`

### Database Queue

- [ ] Job persists across app restart (enqueue → stop → restart → processed)
- [ ] Two instances sharing same DB process same job exactly once (no duplicate execution)

### Redis Cluster Cache

- [ ] `ForgetByPrefixAsync` evicts keys from all cluster nodes
- [ ] Keys hashing to different nodes all cleared

### Scheduler Cluster Mode

- [ ] Two instances with shared DB store: job fires exactly once (no duplicate execution)

---

## Phase 11: CI/CD & Packaging

### GitHub Actions

- [ ] Create `.github/workflows/ci.yml`
- [ ] Stage 1: `dotnet build --configuration Release`
- [ ] Stage 2: Unit tests (`dotnet test tests/DotInfraKit.*.Tests/`)
- [ ] Stage 3: Integration tests (`dotnet test tests/DotInfraKit.IntegrationTests/`) — requires Docker
- [ ] Stage 4: Coverage threshold — 80% line coverage per module via Coverlet
- [ ] Stage 5: Multi-version matrix — `net8.0 | net9.0 | net10.0`

### Coverage Configuration

- [ ] Add Coverlet to each test `.csproj` (`CollectCoverage=true`, `CoverletOutputFormat=cobertura`, `Threshold=80`, `ThresholdType=line`, `ThresholdStat=total`)

### NuGet Packaging

- [ ] Configure `DotInfraKit` meta-package (references Scheduler + Queue + Cache)
- [ ] Set package metadata for all packages: description, tags, license (MIT), icon, README
- [ ] Verify `using DotInfraKit;` covers all extension methods (`AddJobScheduler`, `AddJobQueue`, `AddAppCache`)
- [ ] Verify module namespaces: `DotInfraKit.Scheduler`, `DotInfraKit.Queue`, `DotInfraKit.Cache`
- [ ] Create `CHANGELOG.md` and `LICENSE` files
- [ ] Test local NuGet pack: `dotnet pack --configuration Release`
