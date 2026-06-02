# Publishing DotInfraKit Packages to NuGet

DotInfraKit publishes **3 NuGet packages** from this repository. The production sub-projects (Queue, Cache, Scheduler, Redis driver, Database driver, Monitoring) are bundled as DLLs directly inside `DotInfraKit.nupkg` — consumers install one package and get the full toolkit.

| Package | Contents | Who installs it |
|---------|----------|----------------|
| `DotInfraKit` | 7 DLLs: the main assembly + all 6 sub-project assemblies | Production app code |
| `DotInfraKit.Testing` | 3 DLLs: Testing + Queue + Cache | Test projects |
| `DotInfraKit.Testing.FluentAssertions` | Testing extension; depends on `DotInfraKit.Testing` | Test projects using FluentAssertions |

---

## Prerequisites

- .NET SDK 8.0+
- nuget.org account with an API key ([create one here](https://www.nuget.org/account/apikeys))
- All tests passing locally

---

## 1. Decide the Version

DotInfraKit uses [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

| Change type | Example bump |
|-------------|-------------|
| Breaking API change | `1.0.0` → `2.0.0` |
| New feature, backward-compatible | `1.0.0` → `1.1.0` |
| Bug fix | `1.0.0` → `1.0.1` |

Use a pre-release suffix to validate before going stable:

```
1.1.0-preview.1
1.1.0-beta.1
```

Pre-release packages do not appear as the stable version to consumers.

---

## 2. Pre-Publish Checklist

- [ ] All unit tests pass: `dotnet test`
- [ ] `CHANGELOG.md` updated — add the new version, date, and summary of changes
- [ ] Git working tree is clean: `git status` shows no uncommitted changes
- [ ] Tag the release commit:

```bash
git tag v1.0.0
git push origin v1.0.0
```

---

## 3. Build in Release Configuration

```bash
dotnet build --configuration Release
```

This compiles all 3 packable projects and their bundled sub-projects.

---

## 4. Pack All 3 Packages

```bash
VERSION=1.0.0

dotnet pack src/DotInfraKit/DotInfraKit.csproj \
  --configuration Release -p:Version=$VERSION --no-build

dotnet pack src/DotInfraKit.Testing/DotInfraKit.Testing.csproj \
  --configuration Release -p:Version=$VERSION --no-build

dotnet pack src/DotInfraKit.Testing.FluentAssertions/DotInfraKit.Testing.FluentAssertions.csproj \
  --configuration Release -p:Version=$VERSION --no-build
```

**Verify the 3 packages were produced:**

```bash
find src -name "*.nupkg" -path "*/Release/*"
```

Expected output:

```
src/DotInfraKit/bin/Release/DotInfraKit.1.0.0.nupkg
src/DotInfraKit.Testing/bin/Release/DotInfraKit.Testing.1.0.0.nupkg
src/DotInfraKit.Testing.FluentAssertions/bin/Release/DotInfraKit.Testing.FluentAssertions.1.0.0.nupkg
```

**Optional — verify bundle contents of the main package:**

```bash
unzip -l src/DotInfraKit/bin/Release/DotInfraKit.1.0.0.nupkg
```

The `lib/net8.0/` folder should contain 7 DLL files:
`DotInfraKit.dll`, `DotInfraKit.Queue.dll`, `DotInfraKit.Cache.dll`, `DotInfraKit.Scheduler.dll`,
`DotInfraKit.Queue.Redis.dll`, `DotInfraKit.Queue.Database.dll`, `DotInfraKit.Queue.Monitoring.dll`

---

## 5. Publish to NuGet.org

Packages must be pushed **in dependency order**: DotInfraKit first (since Testing.FluentAssertions depends on Testing, and Testing depends on DotInfraKit's runtime deps).

```bash
export NUGET_API_KEY=<your-api-key>
export VERSION=1.0.0

# 1. Production bundle
dotnet nuget push src/DotInfraKit/bin/Release/DotInfraKit.${VERSION}.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate

# 2. Test helpers
dotnet nuget push src/DotInfraKit.Testing/bin/Release/DotInfraKit.Testing.${VERSION}.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate

# 3. FluentAssertions extension (depends on DotInfraKit.Testing)
dotnet nuget push src/DotInfraKit.Testing.FluentAssertions/bin/Release/DotInfraKit.Testing.FluentAssertions.${VERSION}.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate
```

`--skip-duplicate` silently skips the package if that version already exists — safe to re-run.

---

## 6. Post-Publish Verification

nuget.org indexes new packages within ~15 minutes.

1. Check the package page: `https://www.nuget.org/packages/DotInfraKit`
2. Create a fresh test project and install the production package:

```bash
mkdir /tmp/dotinfrakit-verify && cd /tmp/dotinfrakit-verify
dotnet new console
dotnet add package DotInfraKit --version 1.0.0
dotnet build
```

3. Confirm all types from sub-modules are accessible (e.g., `IQueueService`, `ICacheService`, `IJobScheduler`).

---

## 7. Automated Publishing via GitHub Actions

Store your API key as a repository secret named `NUGET_API_KEY` (Settings → Secrets → Actions).

Create `.github/workflows/publish.yml`:

```yaml
name: Publish NuGet Packages

on:
  push:
    tags:
      - 'v*'

jobs:
  publish:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Run tests
        run: dotnet test

      - name: Extract version from tag
        id: version
        run: echo "VERSION=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"

      - name: Build Release
        run: dotnet build --configuration Release

      - name: Pack packages
        env:
          VERSION: ${{ steps.version.outputs.VERSION }}
        run: |
          dotnet pack src/DotInfraKit/DotInfraKit.csproj \
            --configuration Release -p:Version=$VERSION --no-build
          dotnet pack src/DotInfraKit.Testing/DotInfraKit.Testing.csproj \
            --configuration Release -p:Version=$VERSION --no-build
          dotnet pack src/DotInfraKit.Testing.FluentAssertions/DotInfraKit.Testing.FluentAssertions.csproj \
            --configuration Release -p:Version=$VERSION --no-build

      - name: Push packages
        env:
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
          VERSION: ${{ steps.version.outputs.VERSION }}
        run: |
          dotnet nuget push src/DotInfraKit/bin/Release/DotInfraKit.${VERSION}.nupkg \
            --api-key "$NUGET_API_KEY" \
            --source https://api.nuget.org/v3/index.json --skip-duplicate

          dotnet nuget push src/DotInfraKit.Testing/bin/Release/DotInfraKit.Testing.${VERSION}.nupkg \
            --api-key "$NUGET_API_KEY" \
            --source https://api.nuget.org/v3/index.json --skip-duplicate

          dotnet nuget push src/DotInfraKit.Testing.FluentAssertions/bin/Release/DotInfraKit.Testing.FluentAssertions.${VERSION}.nupkg \
            --api-key "$NUGET_API_KEY" \
            --source https://api.nuget.org/v3/index.json --skip-duplicate
```

**Trigger a release:**

```bash
git tag v1.0.0
git push origin v1.0.0
```

The workflow runs automatically: tests → build → pack → push all 3 packages.

---

## 8. Yanking a Bad Release

nuget.org does not support permanent deletion. You can **unlist** a version so it no longer appears in search results. Existing consumers who already installed the version are unaffected.

Via the nuget.org web UI:
1. Go to the package page → **Manage package**
2. Select the version → **Unlist**

Via CLI:

```bash
dotnet nuget delete DotInfraKit 1.0.0 \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --non-interactive
```

This unlists the package — it does not delete it permanently.

---

## Quick Reference

| Task | Command |
|------|---------|
| Build Release | `dotnet build --configuration Release` |
| Pack all 3 | See §4 — pack each project with `--no-build` |
| Push all 3 | See §5 — push in order with `--skip-duplicate` |
| Tag release | `git tag vX.Y.Z && git push origin vX.Y.Z` |
| Unlist version | `dotnet nuget delete <PackageId> X.Y.Z --api-key $KEY --source ...` |
