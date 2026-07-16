---
issue: 3
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/3
branch: 3_service-separation
base: master
plan: docs/plans/3_service-separation-plan.md
---

# Handoff: Service separation — independent processes, independent SQLite databases

## Release Notes

This PR stands up `EventLedger.Gateway` and `EventLedger.AccountService`
as independently runnable ASP.NET Core processes for the first time —
until now the repo contained documentation and tooling only, no
application code. Each service now has a working `Program.cs` (a thin
orchestrator: all DI and logging setup routes through a single
per-service extension method, not inline), structured JSON logging via
Serilog with automatic trace-ID correlation on every log line, and a
`/health` endpoint.

`/health` is intentionally minimal right now — it confirms the process is
up but doesn't check database connectivity, since neither service has a
database yet. That's not an oversight: it's tracked, and the follow-up
story upgrades this exact route in place once there's a database to check
against, rather than introducing a second route later.

Every piece here was built test-first (failing test confirmed before the
implementation that made it pass), and the branch went through two
independent automated review passes — a code-quality sweep and a
five-perspective correctness/security/testing/maintainability review —
before this PR was opened. All resulting fixes are already included, with
a full record of what was checked and why kept alongside the code in
`docs/reviews/3_service-separation.json`.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| Project scaffolding (`.csproj`, `.sln`) | Small — purely additive, no existing code touched | Confirm both services build and solution references are correct | `dotnet build` verified clean, 0 warnings/errors |
| `Program.cs` / DI wiring shape | Medium — every future story builds on this pattern | Confirm the orchestrator convention (no inline setup in `Program.cs`) actually holds, since stories #2/#4/#6 each depend on it staying stable | A self-violation of this exact rule was caught and fixed during review (Serilog/`AddControllers()` had been added inline); now verified against the documented convention |
| Structured logging / trace correlation | Small — logging only, no behavior change | Confirm `TraceId` actually appears on real request logs, not just asserted in prose | Empirically verified via a live diagnostic test capturing actual JSON output, not just documentation |
| `/health` endpoint | Small — a deliberate, documented partial implementation | Confirm the DB-check gap is intentional and tracked, not an oversight | Documented in three places: the plan's Known Constraints, a note added to `architecture/observability.md` itself, and the follow-up story's scope |
| Test suite reliability | Small — test-only | Confirm the parallel-test-execution fix (`DisableTestParallelization`) addresses a real cause, not a band-aid | Root cause identified and documented: shared global state (`Console.Out`, static `Log.Logger`) racing under xUnit's default parallelism |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| `WebApplicationFactory<Program>` boots without throwing (Gateway) | written | `GatewayBootTests.cs` — strengthened during review to assert `IsSuccessStatusCode` instead of an always-true `NotNull` check |
| `WebApplicationFactory<Program>` boots without throwing (Account Service) | written | `AccountServiceBootTests.cs` — same strengthening |
| Request produces a JSON log line with `ServiceName` + `TraceId` (Gateway) | written | `GatewayLoggingTests.cs` — route changed from `/` to `/health` and assertion changed from substring/regex to structured JSON parsing during review |
| Request produces a JSON log line with `ServiceName` + `TraceId` (Account Service) | written | `AccountServiceLoggingTests.cs` — same |
| `GET /health` returns `200 {"status":"ok"}` (Gateway) | written | `HealthControllerTests.cs` |
| `GET /health` returns `200 {"status":"ok"}` (Account Service) | written | `HealthControllerTests.cs` |

No unplanned tests were added beyond what the plan's phases already called for.

### What's Not Tested

**DEP-3 (separate DB files, no shared connection string)** has no automated test in this story, because no `DbContext` exists yet on either service — there's nothing to point at two separate files. What DEP-3 is really about at this stage (each service owning its own isolated `Infrastructure/` folder, no shared project reference between them) is a structural property verified by the review pass, not runtime behavior with something to assert against yet; it becomes testable the moment the next story adds the `DbContext` classes into the folders this story created.

**Multi-process startup** (literally running `dotnet run` for each service in separate OS processes and confirming independent startup) wasn't exercised by this automated suite. `WebApplicationFactory` tests boot the ASP.NET Core pipeline in-process, which is the standard, faster proxy for "the service starts" and is consistent with how every test in this suite is built — not a gap specific to this story.

**The `Log.Fatal`/`Log.CloseAndFlush` crash-handling path** added during review has no dedicated test, deliberately — triggering it would require forcing `WebApplicationBuilder.Build()` to throw, which isn't a realistic enough scenario to warrant a dedicated test at this story's scope.
