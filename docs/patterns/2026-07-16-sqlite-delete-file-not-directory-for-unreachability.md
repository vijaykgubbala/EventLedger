---
title: Simulate SQLite unreachability by deleting the file after startup, not a wrapping directory
date: 2026-07-16
related: [../plans/5_observability-plan.md, ../simplify-patterns.md]
---

## Pattern

A test that needs a real `Database.CanConnectAsync()` (or any query) to
genuinely fail — not mocked, not stubbed — needs a SQLite target that
exists at host startup (so `Database.EnsureCreated()` succeeds and the
app boots) but becomes unreachable afterward, isolating the failure to
the request under test rather than crashing the whole host.

Two things about *how* to break it turned out to matter more than
expected:

1. **Deleting a nonexistent directory in the connection string before
   startup crashes the whole host**, not just the endpoint under test —
   `EnsureCreated()` runs during boot and throws if it can't create the
   database file, so the `WebApplicationFactory` never finishes starting
   and the test never reaches the endpoint it meant to exercise.
2. **A single `File.Delete(dbPath)` after startup is sufficient** —
   wrapping the file in its own temp directory and deleting the whole
   directory (`Directory.CreateDirectory`/`Directory.Delete(recursive:
   true)`) adds setup/teardown ceremony with no extra isolation benefit,
   since a unique GUID-suffixed file name already provides per-test
   isolation. `SqliteConnection.ClearAllPools()` followed by deleting just
   the file reliably fails the next connection attempt — SQLite does
   **not** silently recreate the missing file the way it would for a
   connection string that never pointed at an existing file in the first
   place. The most likely reason: WAL-mode sidecar files (`-wal`/`-shm`,
   left over from `EnsureCreated()`'s `PRAGMA journal_mode = 'wal'`) still
   reference the now-deleted main file, and SQLite's reopen attempt fails
   against that stale state rather than starting clean.

## Guidance

For a "genuinely broken DB connection" integration test:

1. Point the `DbContext` at a real, valid temp file path from the start —
   don't pre-break the path.
2. Force host startup while the file still exists (accessing
   `factory.Services` on a `WebApplicationFactory` is enough to trigger
   this without needing a real request yet).
3. Only then call `SqliteConnection.ClearAllPools()` followed by
   `File.Delete(dbPath)` — no wrapping directory needed.
4. Issue the request under test and assert the failure-path behavior.

Verified reliable across 5 consecutive manual runs before relying on it
in a committed test — this class of test is exactly the kind where a
single lucky pass can hide a race, so don't trust one green run.

## Examples

**Wrong** — unnecessary directory wrapper, and (if attempted before
startup) crashes the host instead of reaching the endpoint:

```csharp
var tempDir = Path.Combine(Path.GetTempPath(), $"gateway-health-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDir);
var connectionString = $"Data Source={Path.Combine(tempDir, "test.db")}";
// ... factory built and started here ...
SqliteConnection.ClearAllPools();
Directory.Delete(tempDir, recursive: true);
```

**Right** — a single file, deleted after startup:

```csharp
var dbPath = Path.Combine(Path.GetTempPath(), $"gateway-health-test-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={dbPath}";

await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    builder.ConfigureServices(services =>
    {
        services.RemoveAll<DbContextOptions<GatewayDbContext>>();
        services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(connectionString));
    }));

_ = factory.Services; // forces EnsureCreated() to run while the file exists

SqliteConnection.ClearAllPools();
File.Delete(dbPath);

using var client = factory.CreateClient();
var response = await client.GetAsync("/health"); // now genuinely fails the DB check
```

See `GetHealth_DatabaseUnreachable_Returns200WithDegradedStatus` in
`tests/EventLedger.Gateway.Tests/HealthControllerTests.cs` for the
applied fix.
