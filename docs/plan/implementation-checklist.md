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

## Phase 3: DotInfraKit.Queue (Core) ✅

### Interfaces & Models

- [x] Define `IQueueJob<TPayload>` interface (`Task ExecuteAsync(TPayload, JobContext, CancellationToken)`)
- [x] Define `IQueueService` interface with two `EnqueueAsync<TJob, TPayload>` overloads returning `Task<Guid>`
- [x] Define `IDlqService` interface (`GetDeadJobsAsync`, `RetryAsync`, `RetryAllAsync`, `DeleteAsync`, `DeleteAllAsync`)
- [x] Define `JobContext` record/class (`JobId`, `QueueName`, `AttemptNumber`, `MaxAttempts`, `EnqueuedAt`)
- [x] Define `EnqueueOptions` class (`Priority`, `Delay`, `RunAt`)
- [x] Define `BackoffType` enum (`Exponential`, `Fixed`, `Linear`)
- [x] Define `DlqJobRecord` POCO (`Id`, `QueueName`, `JobType`, `Payload`, `Attempts`, `ErrorMessage`, `CreatedAt`, `DeadAt`)

### Retry Policy

- [x] Implement `RetryPolicy` with `CalculateDelay(attempt)`:
  - `Exponential`: `2^(attempt-1) × initialDelayMs`
  - `Fixed`: `initialDelayMs` always
  - `Linear`: `attempt × initialDelayMs`

### Memory Driver

- [x] Implement `MemoryQueueDriver` using `System.Threading.Channels`
- [x] Track delayed jobs in in-memory sorted list by `RunAt`; move to channel when ready

### Worker Service

- [x] Implement `QueueWorkerService : BackgroundService`
- [x] Use `SemaphoreSlim(concurrency)` for parallel job execution
- [x] Worker loop: dequeue → acquire semaphore → `Task.Run(ProcessNextJobAsync)` → release semaphore
- [x] Support multiple worker instances (`count`) per queue
- [x] Resolve job type via `Type.GetType(jobRecord.JobType)` + `IServiceScopeFactory.CreateScope()`
- [x] Log `InvalidOperationException` with clear message if job type not found; discard + continue

### Stuck Job Sweeper

- [x] Implement `StuckJobSweeperService : BackgroundService`
- [x] Default `LockTimeout`: 5 minutes; polling interval: `LockTimeout / 2`
- [x] On timeout: increment `attempts`, clear `locked_at`/`locked_by`
- [x] If `attempts >= max_attempts` after reset → move to DLQ (or discard if DLQ not enabled)
- [x] Log `Warning` when job permanently discarded (no DLQ)

### Delayed Job Sweeper

- [x] Implement `DelayedJobSweeperService : BackgroundService`
- [x] Default polling interval: 5 seconds
- [x] Query `next_run_at <= UtcNow AND status = 'pending'` → move to ready pool

### Dead-Letter Queue

- [x] Implement `InMemoryDlqService` using `ConcurrentDictionary`
- [x] Register `IDlqService` only when `EnableDeadLetterQueue()` is called

### DI Registration

- [x] Implement `AddJobQueue(IServiceCollection, Action<QueueOptions>)` extension in `DotInfraKit` namespace
- [x] Implement `UseDefaultQueue(Action<QueueBuilder>)` and `AddQueue(name, Action<QueueBuilder>)` on `QueueOptions`
- [x] Implement `QueueBuilder` methods: `UseMemoryDriver`, `Workers(count, concurrency)`, `Retry(maxAttempts, BackoffType, initialDelayMs)`, `EnableDeadLetterQueue()`, `LockTimeout(TimeSpan)`, `DelayedJobPollingInterval(TimeSpan)`
- [x] Log one-time startup warning when `Priority > 0` used with Memory driver

### Unit Tests (20 passing)

- [x] `RetryPolicyTests` — all BackoffType × attempt combinations
- [x] `MemoryQueueDriverTests` — enqueue/dequeue/complete/fail/stuck/delayed/DLQ flows
- [x] `QueueWorkerServiceTests` — execute called, exception → fail, exception → DLQ, unresolvable type handled

---

## Phase 4: DotInfraKit.Queue.Redis ✅

### Redis Connection

- [x] Implement `UseRedis(Action<RedisOptions>)` on `QueueBuilder` via extension method (single node)
- [x] Implement `UseRedisSentinel(Action<RedisSentinelOptions>)` on `QueueBuilder` via extension method
- [x] Implement `UseRedisCluster(Action<RedisClusterOptions>)` on `QueueBuilder` via extension method
- [x] Support `KeyPrefix` on all Redis option types; queue keys follow `{KeyPrefix}queue:{queueName}`

### Redis Driver

- [x] Implement `RedisQueueDriver` backed by Redis `LIST` (RPUSH enqueue, LPOP poll-dequeue)
- [x] Store job payload in `{KeyPrefix}job:{jobId:N}` (Redis String, JSON QueueJobEntry)
- [x] Track processing in `{KeyPrefix}processing:{name}` (Sorted Set, score = LockedAt epoch)
- [x] Track delayed in `{KeyPrefix}delayed:{name}` (Sorted Set, score = NextRunAt epoch)
- [x] `locked_by` format: worker ID string passed from `QueueWorkerService`

### Redis DLQ

- [x] Implement `RedisDlqService`
- [x] Dead jobs stored in `{KeyPrefix}dlq:{queueName}` Redis Hash (`{jobId:N}` → JSON `DlqJobRecord`)
- [x] Implement all `IDlqService` operations against Redis Hash

### Stuck Job Sweeper (Redis)

- [x] Use `ZRANGEBYSCORE {prefix}processing:{name} 0 {epoch}` to find stuck jobs efficiently
- [x] On timeout: increment `attempts`, clear `locked_at`/`locked_by`, re-push to queue LIST or move to DLQ

### Architecture

- [x] `InternalsVisibleTo("DotInfraKit.Queue.Redis")` on core package — lets Redis package access internal driver interface
- [x] `QueueDriverRegistration` factory pattern — driver + DLQ service created together, memory and Redis both use same `AddJobQueue()` path

### Integration Tests (requires Docker / OrbStack)

- [x] `RedisQueueDriverTests` — 9 tests covering all `IQueueDriver` methods via Testcontainers.Redis

---

## Phase 5: DotInfraKit.Queue.Database ✅

### Entity & Migration

- [x] Define `QueueJobRecord` entity with all columns: `Id`, `QueueName`, `JobType`, `Payload`, `Status`, `Attempts`, `MaxAttempts`, `NextRunAt`, `LockedAt`, `LockedBy`, `ErrorMessage`, `CreatedAt`, `CompletedAt`
- [x] Implement `AddDotInfraKitQueue(ModelBuilder)` extension: snake_case column names, default values, dequeue index
- [x] Status values: `pending | processing | completed | failed | dead`

### Database Driver

- [x] Implement `DatabaseQueueDriver<TContext>` using EF Core + optimistic locking (`WHERE locked_at IS NULL` → 1 row affected)
- [x] Dequeue query: `ORDER BY priority DESC, next_run_at ASC` (priority only honored here)
- [x] Implement `UseDatabaseDriver<TContext>(Action<DatabaseDriverOptions>?)` on `QueueBuilder` via extension method
- [x] `IDbContextFactory<TContext>` resolved from DI; driver registered via provider-based factory pattern

### AutoMigrate

- [x] Implement `AutoMigrate(bool allowInProduction = false)` on `DatabaseDriverOptions`
- [x] Check `IHostEnvironment.IsProduction()` at startup; throw `InvalidOperationException` if production and `allowInProduction` is false

### Database DLQ

- [x] Implement `DatabaseDlqService<TContext>` querying `QueueJobRecord` where `status = 'dead'`
- [x] `RetryAsync`: reset `status = 'pending'`, `attempts = 0`, clear `locked_at`/`locked_by`

### Architecture

- [x] `InternalsVisibleTo("DotInfraKit.Queue.Database")` on core package
- [x] `QueueDriverRegistration` extended with `CreateDriverFromProvider` / `CreateDlqServiceFromProvider` for DI-based driver creation
- [x] `QueueServiceExtensions` refactored to use keyed services (`AddKeyedSingleton<IQueueDriver>`)
- [x] `QueueBuilder._startupRegistrations` for AutoMigrate hosted service registration

### Integration Tests (SQLite, no Docker)

- [x] `DatabaseQueueDriverTests` — 10 tests covering all `IQueueDriver` methods + optimistic lock concurrency

---

## Phase 6: DotInfraKit.Cache ✅

### Interface

- [x] Define `ICacheService` interface: `GetOrSetAsync<T>`, `GetAsync<T>`, `SetAsync<T>`, `ForgetAsync`, `ForgetByPrefixAsync`, `ExistsAsync`

### Memory Driver

- [x] Implement `MemoryCacheDriver` wrapping `IMemoryCache`
- [x] Maintain `ConcurrentDictionary<string, byte>` key registry alongside cache
- [x] `SetAsync` → insert key into registry
- [x] `ForgetAsync` → remove from registry + cache
- [x] `ForgetByPrefixAsync` → prefix-scan registry (O(n)), delete matching keys from both registry and cache

### Redis Driver (Single Node)

- [x] Implement `RedisCacheDriver` using `StackExchange.Redis`
- [x] Support `KeyPrefix`, `Password`, `Database`
- [x] `ForgetByPrefixAsync` → `SCAN` on single node + batch `DEL`

### Redis Sentinel Driver

- [x] `CacheRedisSentinelOptions` with `ServiceName`, `Endpoints[]`, `Password`, `Database`, `KeyPrefix`
- [x] `CacheBuilder.UseRedisSentinel()` builds sentinel mux → master mux → same `RedisCacheDriver`
- [x] `ForgetByPrefixAsync` → `SCAN` primary node + batch `DEL` (same `RedisCacheDriver`)

### Redis Cluster Driver

- [x] Implement `RedisClusterCacheDriver` (composition over `RedisCacheDriver`)
- [x] `ForgetByPrefixAsync` → scan **all master nodes** in parallel (`Parallel.ForEachAsync`); collect matched keys → batch `DEL`

### Auto-Invalidation Middleware

- [x] `CacheAutoInvalidationConfig` internal singleton holds prefix
- [x] `UseAppCache()` on `IApplicationBuilder` wires `Use()` lambda: POST/PUT/PATCH/DELETE → `ForgetByPrefixAsync(prefix)` after response
- [x] Register via `EnableAutoInvalidation(prefix)` on `CacheBuilder`

### DI Registration

- [x] `AddAppCache(IServiceCollection, Action<CacheBuilder>)` extension in `DotInfraKit` namespace
- [x] `CacheBuilder` fluent methods: `UseMemory()`, `UseRedis()`, `UseRedisSentinel()`, `UseRedisCluster()`, `WithDefaultExpiry(TimeSpan)`, `EnableAutoInvalidation(prefix)`
- [x] `UseMemory()` auto-calls `services.AddMemoryCache()` via `_isMemory` flag

### Architecture Decisions

- [x] Single `RedisCacheDriver` handles both single-node and Sentinel (same `IConnectionMultiplexer` API)
- [x] `RedisClusterCacheDriver` uses composition (not inheritance) — delegates all ops to inner `RedisCacheDriver`, overrides only `ForgetByPrefixAsync`
- [x] `AssemblyInfo.cs` with `InternalsVisibleTo("DotInfraKit.Cache.Tests")`

### Unit Tests (MemoryCacheDriverTests — 6 tests, no Docker)

- [x] `GetOrSetAsync_CallsFactoryOnMiss`
- [x] `GetOrSetAsync_SkipsFactoryOnHit`
- [x] `ForgetAsync_RemovesKey`
- [x] `ForgetByPrefixAsync_RemovesMatchingKeysOnly`
- [x] `ExistsAsync_ReturnsTrueForCachedKey`
- [x] `DefaultExpiry_AppliedWhenNoExpiryPassed`

---

## Phase 7: DotInfraKit.Testing ✅

- [x] Implement `FakeQueueService : IQueueService`
  - Records enqueued jobs in-memory via `object?` payload storage, does NOT execute them
  - `AssertEnqueued<TJob>()` — throws `InvalidOperationException` if none found
  - `AssertEnqueued<TJob, TPayload>(Func<TPayload, bool> predicate)` — cast + predicate match
  - `AssertNotEnqueued<TJob>()` — throws if any found
  - `AssertEnqueuedCount<TJob>(int expected)` — exact count assertion
- [x] Implement `FakeCacheService : ICacheService`
  - In-memory `Dictionary<string, object?>` store (no expiry — not needed in tests)
  - `GetCallCount(key)` — times `GetAsync` / `GetOrSetAsync` called for key
  - `FactoryCallCount(key)` — times factory was actually invoked (cache miss count)

### Architecture Decisions

- [x] No InternalsVisibleTo needed — fakes only implement public interfaces
- [x] No thread-safety (plain `List<T>`, `Dictionary`) — tests are single-threaded
- [x] Assertion methods throw `InvalidOperationException` — no test framework dependency

### Unit Tests (`DotInfraKit.Testing.Tests` — 13 tests)

**FakeQueueServiceTests (7)**
- [x] `EnqueueAsync_RecordsJob_AssertEnqueuedPasses`
- [x] `AssertEnqueued_ThrowsWhenJobNotEnqueued`
- [x] `AssertEnqueued_WithPredicate_MatchesPayload`
- [x] `AssertEnqueued_WithPredicate_ThrowsWhenNoMatch`
- [x] `AssertNotEnqueued_PassesWhenNothingEnqueued`
- [x] `AssertNotEnqueued_ThrowsWhenJobEnqueued`
- [x] `AssertEnqueuedCount_MatchesExactCount`

**FakeCacheServiceTests (6)**
- [x] `SetAsync_ThenGetAsync_ReturnsValue`
- [x] `ForgetAsync_RemovesKey`
- [x] `ForgetByPrefixAsync_RemovesMatchingKeysOnly`
- [x] `GetOrSetAsync_IncrementsCounts`
- [x] `GetAsync_IncrementsGetCallCount`
- [x] `ExistsAsync_ReturnsTrueOnlyForStoredKey`

---

## Phase 8: DotInfraKit.Testing.FluentAssertions ✅

- [x] `FluentAssertions` NuGet dependency already in project csproj (not in base Testing package)
- [x] Implement `FakeQueueServiceAssertions` with fluent syntax:
  - `fakeQueue.Should().HaveEnqueued<TJob>()` → `HaveEnqueuedChain<TJob>` (carries TJob type)
  - `.WithPayload<TPayload>(Func<TPayload, bool> predicate)` → `AndConstraint<FakeQueueServiceAssertions>`
  - `.NotHaveEnqueued<TJob>()` → `AndConstraint<FakeQueueServiceAssertions>`
  - `.HaveEnqueuedCount<TJob>(int)` → `AndConstraint<FakeQueueServiceAssertions>`
- [x] Implement `FakeCacheServiceAssertions` with fluent syntax:
  - `.HaveKey(string)` / `.NotHaveKey(string)`
  - `.HaveGetCallCount(string key, int expected)`
  - `.HaveFactoryCallCount(string key, int expected)`

### Architecture Decisions

- [x] `HaveEnqueuedChain<TJob>` extends `AndConstraint<FakeQueueServiceAssertions>` — carries TJob for `WithPayload`
- [x] `TryAssert(Action)` wraps public `Assert*` methods — converts `InvalidOperationException` to `false` for `ForCondition`
- [x] FA failures via `Execute.Assertion.BecauseOf(...).ForCondition(...).FailWith(...)` — standard FA7 pattern
- [x] Tests in `DotInfraKit.Testing.Tests` (added ProjectReference to FluentAssertions project)

### Tests (11 new, in DotInfraKit.Testing.Tests)

**FakeQueueServiceAssertionsTests (6)**
- [x] `HaveEnqueued_PassesWhenJobEnqueued`
- [x] `HaveEnqueued_FailsWhenJobNotEnqueued`
- [x] `HaveEnqueued_WithPayload_PassesWhenMatches`
- [x] `HaveEnqueued_WithPayload_FailsWhenNoMatch`
- [x] `NotHaveEnqueued_PassesWhenEmpty`
- [x] `NotHaveEnqueued_FailsWhenEnqueued`

**FakeCacheServiceAssertionsTests (5)**
- [x] `HaveKey_PassesWhenKeyExists`
- [x] `HaveKey_FailsWhenKeyAbsent`
- [x] `NotHaveKey_PassesAfterForget`
- [x] `HaveGetCallCount_MatchesActualCount`
- [x] `HaveFactoryCallCount_MatchesActualCount`

---

## Phase 9: Unit Tests ✅

### DotInfraKit.Scheduler.Tests

- [x] Add xUnit, FluentAssertions, NSubstitute, Microsoft.Extensions.Logging.Abstractions
- [x] Test `ScheduleBuilder.ToCronExpression()` for all schedule methods (Theory/InlineData)
- [x] Test each `IScheduledJob` implementation in isolation (no Quartz.NET required)
- [x] Test `WithCronFromConfig` throws on missing key
- [x] Test `IScheduledJobExceptionHandler` is invoked on job failure

### DotInfraKit.Queue.Tests

- [x] Add xUnit, FluentAssertions, NSubstitute, EFCore.InMemory
- [x] Test job `ExecuteAsync` called with correct payload
- [x] Test exception propagation from job
- [x] Test `RetryPolicy.CalculateDelay` for all `BackoffType` values (Theory/InlineData)
- [x] Test Memory driver: enqueue → dequeue order (FIFO)
- [x] Test `Workers(count > 1)` logs warning with Memory driver — `QueueWorkerService` gains optional `totalWorkerCount` param; warns if `> 1` and driver is `MemoryQueueDriver`
- [x] Test `Priority > 0` with Memory/Redis driver logs warning — warning already in `QueueService.EnqueueAsync`; `QueueServiceTests` added

### DotInfraKit.Cache.Tests

- [x] Add xUnit, FluentAssertions
- [x] Test `GetOrSetAsync` calls factory on miss, skips on hit
- [x] Test `ForgetAsync` removes key
- [x] Test `ForgetByPrefixAsync` removes only matching keys (Memory driver)
- [x] Test `ExistsAsync` returns correct bool
- [x] Test `DefaultExpiry` applied when no expiry passed

---

## Phase 10: Integration Tests ✅

- [x] Add Testcontainers.Redis, Testcontainers.MsSql, Microsoft.EntityFrameworkCore.Sqlite
- [x] Create `docker-compose.test.yml` for 3-node Redis Cluster (CI)
- [x] Mark Redis Cluster tests with `[Trait("Category", "RedisCluster")]`

### Redis Queue

- [x] `EnqueueAsync` → job picked up and executed by worker (TaskCompletionSource pattern)
- [x] Failing job retried and moved to DLQ after `maxAttempts`

### Database Queue

- [x] Job persists across app restart (enqueue → stop → restart → processed)
- [x] Two instances sharing same DB process same job exactly once (no duplicate execution)

### Redis Cluster Cache

- [x] `ForgetByPrefixAsync` evicts keys from all cluster nodes
- [x] Keys hashing to different nodes all cleared

### Scheduler Cluster Mode

- [x] Two instances with shared DB store: job fires exactly once (no duplicate execution)

---

## Phase 11: Documentation

- [ ] Create `README.md` with overview, features, getting started, example usage for each module on each .NET version
- [ ] Create `CHANGELOG.md` with initial version entry
- [ ] Create `LICENSE` file with MIT license text

---

## Phase 12: CI/CD & Packaging

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
