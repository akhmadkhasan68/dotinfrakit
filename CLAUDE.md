# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build

# Run all unit tests (no Docker required)
dotnet test tests/DotInfraKit.Queue.Tests/
dotnet test tests/DotInfraKit.Cache.Tests/
dotnet test tests/DotInfraKit.Scheduler.Tests/
dotnet test tests/DotInfraKit.Testing.Tests/
dotnet test tests/DotInfraKit.Queue.Monitoring.Tests/

# Run a single test
dotnet test tests/DotInfraKit.Queue.Tests/ --filter "FullyQualifiedName~RetryPolicyTests"

# Integration tests (requires Docker/OrbStack for Redis/MSSQL via Testcontainers)
dotnet test tests/DotInfraKit.IntegrationTests/

# Redis Cluster integration tests (requires docker-compose)
docker-compose -f docker-compose.test.yml up -d
REDIS_CLUSTER_ENDPOINTS=localhost:7001,localhost:7002,localhost:7003 \
  dotnet test tests/DotInfraKit.IntegrationTests/ --filter "Category=RedisCluster"
docker-compose -f docker-compose.test.yml down

# Pack NuGet packages
dotnet pack --configuration Release
```

## Architecture

### Solution structure

- `src/DotInfraKit` — meta-package; references all five library packages. Extension methods (`AddJobScheduler`, `AddJobQueue`, `AddAppCache`) live in the `DotInfraKit` namespace even though they are implemented in the module projects.
- `src/DotInfraKit.Scheduler` — Quartz.NET wrapper. Fluent `ScheduleBuilder` → cron expressions. Cluster mode uses `AdoJobStore` (shared relational DB).
- `src/DotInfraKit.Queue` — core queue abstractions, `MemoryQueueDriver`, `QueueWorkerService`, `StuckJobSweeperService`, `DelayedJobSweeperService`.
- `src/DotInfraKit.Queue.Redis` — `RedisQueueDriver` + `RedisDlqService`. Redis key layout: `{prefix}queue:{name}` (LIST), `{prefix}job:{id}` (String), `{prefix}processing:{name}` (Sorted Set), `{prefix}delayed:{name}` (Sorted Set), `{prefix}dlq:{name}` (Hash).
- `src/DotInfraKit.Queue.Database` — `DatabaseQueueDriver<TContext>` via `IDbContextFactory`; optimistic locking via `WHERE locked_at IS NULL`. Auto-migrate support (blocked in production by default).
- `src/DotInfraKit.Queue.Monitoring` — minimal HTTP endpoints wired via `MapQueueMonitoring()`. Uses `IQueueMonitorService` (internal) backed by keyed `IQueueDriver` instances.
- `src/DotInfraKit.Cache` — `ICacheService` abstraction. `MemoryCacheDriver` tracks key registry for prefix-scan. `RedisCacheDriver` handles single-node and Sentinel. `RedisClusterCacheDriver` composes `RedisCacheDriver` and overrides `ForgetByPrefixAsync` to scan all master nodes in parallel.
- `src/DotInfraKit.Testing` — `FakeQueueService` and `FakeCacheService` (in-memory, no test framework dep).
- `src/DotInfraKit.Testing.FluentAssertions` — FluentAssertions extension for fakes (`fakeQueue.Should().HaveEnqueued<TJob>()`).

### Driver registration pattern

`IQueueDriver` is registered as a **keyed singleton** by queue name. `QueueDriverRegistration` carries either eager factory (`CreateDriver`) or provider-based factory (`CreateDriverFromProvider`) — the latter is used by the Database driver which needs DI-resolved `IDbContextFactory`. DLQ service has the same two-factory pattern.

`InternalsVisibleTo` attributes on `DotInfraKit.Queue` expose driver internals to `DotInfraKit.Queue.Redis` and `DotInfraKit.Queue.Database`.

### Worker model

Each queue runs `WorkerCount` independent `QueueWorkerService` instances (hosted services). Each uses a `SemaphoreSlim(concurrency)` for in-flight job limiting. Worker IDs follow `{MachineName}:{queueName}:w{n}`. `StuckJobSweeperService` reclaims jobs locked longer than `LockTimeout` (default 5 min). `DelayedJobSweeperService` promotes jobs whose `NextRunAt ≤ UtcNow` (default poll 5 s).

### Build constraints

- `TreatWarningsAsErrors=true` globally — zero warnings allowed.
- Central package versioning via `Directory.Packages.props`; never add `Version=` to individual `.csproj` files.
- Database integration tests use SQLite (no Docker). Redis tests use Testcontainers (Docker required). Redis Cluster tests need the `docker-compose.test.yml` stack and the `REDIS_CLUSTER_ENDPOINTS` env var.

### Pending work (Phases 11–12)

CI/CD (`github/workflows/ci.yml`), NuGet packaging metadata, multi-version matrix (`net8.0|net9.0|net10.0`), and coverage configuration are not yet implemented.