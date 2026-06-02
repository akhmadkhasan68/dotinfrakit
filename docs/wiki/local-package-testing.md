# Local Package Testing

How to test DotInfraKit package changes locally without publishing to NuGet.

---

## Option A — Project Reference (recommended for iteration)

Replace `<PackageReference>` with `<ProjectReference>` in your consuming app's `.csproj`. No packing or feed setup needed — rebuild picks up the latest source.

```xml
<!-- Before -->
<PackageReference Include="DotInfraKit.Cache" Version="1.0.0" />

<!-- After -->
<ProjectReference Include="/path/to/DotInfraKit/src/DotInfraKit.Cache/DotInfraKit.Cache.csproj" />
```

Repeat for each package you need:

```xml
<ProjectReference Include="/path/to/DotInfraKit/src/DotInfraKit/DotInfraKit.csproj" />
<ProjectReference Include="/path/to/DotInfraKit/src/DotInfraKit.Queue/DotInfraKit.Queue.csproj" />
<ProjectReference Include="/path/to/DotInfraKit/src/DotInfraKit.Queue.Redis/DotInfraKit.Queue.Redis.csproj" />
```

**When done testing**, revert back to `<PackageReference>`.

---

## Option B — Local NuGet Feed (simulates real package consumption)

Use this when you need to test the full package experience — versioning, transitive dependencies, or `nuget.config` behavior.

### Step 1 — Pack

Version is not defined in the `.csproj` files (it's set by CI), so pass it explicitly via `-p:Version`:

```bash
# Pack a single package
dotnet pack src/DotInfraKit.Cache/DotInfraKit.Cache.csproj \
    -p:Version=1.0.0-local \
    -o /tmp/local-nuget

# Or pack all packages at once
dotnet pack DotInfraKit.sln \
    -p:Version=1.0.0-local \
    -o /tmp/local-nuget
```

### Step 2 — Register the local feed

Add a `nuget.config` in your consuming project's root (or modify an existing one):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local" value="/tmp/local-nuget" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

### Step 3 — Reference the package

```bash
dotnet add package DotInfraKit.Cache --version 1.0.0-local
```

### Re-packing after changes

NuGet caches packages by exact version string. Increment the suffix each time you re-pack so the consuming project picks up the new build:

```bash
dotnet pack src/DotInfraKit.Cache/DotInfraKit.Cache.csproj \
    -p:Version=1.0.0-local.2 \
    -o /tmp/local-nuget
```

Then update the reference in your consuming project:

```bash
dotnet add package DotInfraKit.Cache --version 1.0.0-local.2
```

---

## Running the existing tests

The solution already has unit and integration tests. Integration tests use Testcontainers (Docker required):

```bash
# All tests
dotnet test DotInfraKit.sln

# Unit tests only (no Docker needed)
dotnet test tests/DotInfraKit.Cache.Tests
dotnet test tests/DotInfraKit.Queue.Tests

# Integration tests (requires Docker)
dotnet test tests/DotInfraKit.IntegrationTests
```
