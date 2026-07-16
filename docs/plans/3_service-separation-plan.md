# Service Separation — Independent Processes, Independent SQLite Databases

**Issue:** #3

## Context

Scaffolds `EventLedger.Gateway` and `EventLedger.AccountService` as two
independently runnable ASP.NET Core projects, per
[docs/brainstorms/3_service-separation-brainstorm.md](../brainstorms/3_service-separation-brainstorm.md).
This is the first code story — nothing upstream to depend on — and issue
#2's plan already assumes this story's scaffold (`.csproj` files,
`Program.cs`, DI container, empty layered folders) exists before it runs.

**Architecture pre-flight result**: one real gap found, not a design
disagreement — see Decisions Made item 2 and Known Constraints below.

## Relevant Learnings

- No `docs/solutions/` yet — expected this early.
- [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md) — checked, largely inapplicable to this story (see Decisions Made item 7).

## Implementation Steps

### Phase 1: Project scaffolding (both services)

- [x] `dotnet new web -o src/EventLedger.Gateway` (minimal template — no generated controllers/Swagger to clean up)
- [x] `dotnet new web -o src/EventLedger.AccountService`
- [ ] `builder.Services.AddControllers()` in each `Program.cs` (MVC controller support is part of the shared ASP.NET Core framework, no extra package) — deferred to Phase 2's `Program.cs` rewrite
- [x] Create `EventLedger.sln` at repo root; add both projects
- [x] Create `Controllers/`, `Application/`, `Domain/`, `Infrastructure/`, `Middleware/` folders in each service per [standards/backend-architecture.md](../../standards/backend-architecture.md)
- [x] `dotnet new xunit -o tests/EventLedger.Gateway.Tests`; add `Microsoft.AspNetCore.Mvc.Testing` package; project reference to `EventLedger.Gateway`
- [x] `dotnet new xunit -o tests/EventLedger.AccountService.Tests`; same, referencing `EventLedger.AccountService`
- [x] Add both test projects to the `.sln`

### Phase 2: `Program.cs` orchestrator + `AppMarker`

- [x] Test: `EventLedger.Gateway.Tests` — constructing a `WebApplicationFactory<Program>` and its client doesn't throw (integration, Phase 2) — confirmed red first (`CS0122: 'Program' is inaccessible`) before `AppMarker.cs` existed
- [x] Test: `EventLedger.AccountService.Tests` — same boot-smoke-test (integration, Phase 2)
- [x] Implement: `src/EventLedger.Gateway/AppMarker.cs` — `public partial class Program { }`, file scope, no enclosing namespace (top-level statements already generate `Program` in the global namespace — a `partial` inside any namespace would create a different, unrelated type)
- [x] Implement: `src/EventLedger.AccountService/AppMarker.cs` — same
- [x] Implement: `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` — `AddGatewayInfrastructure(this WebApplicationBuilder builder)`, currently returns `builder` unchanged (populated by issues #2/#4/#6 — this is the one call site each of those stories adds a line to, so `Program.cs`'s shape never needs to change again)
- [x] Implement: `src/EventLedger.AccountService/Infrastructure/ServiceCollectionExtensions.cs` — `AddAccountServiceInfrastructure(this WebApplicationBuilder builder)`, same
- [x] Implement: `src/EventLedger.Gateway/Program.cs` — orchestrator only: `builder.AddGatewayInfrastructure();`, `builder.Services.AddControllers();`, `app.MapControllers(); app.Run();` (Serilog wiring lands as an addition in Phase 3, not a reshape)
- [x] Implement: `src/EventLedger.AccountService/Program.cs` — same shape

### Phase 3: Structured logging (both services)

- [x] Test: a request produces a JSON log line on stdout containing `ServiceName` and a `TraceId` property (integration, Phase 3) — hits `/` rather than `/health` (Phase 4 doesn't exist yet at this point in the sequence); ASP.NET Core's built-in "Request starting" hosting-diagnostics log fires regardless of whether a route matches, per [standards/logging-dotnet.md](../../standards/logging-dotnet.md)'s note that built-in request logging covers this. **Empirically verified** `Activity.Current`/`TraceId` is populated even pre-OpenTelemetry (real 32-hex-char trace ID observed), confirming the Known Constraints note below rather than just asserting it.
- [x] Implement: Serilog setup in each `Program.cs`, exact snippet from [standards/logging-dotnet.md](../../standards/logging-dotnet.md) — `Enrich.FromLogContext()`, `.Enrich.WithProperty("ServiceName", "EventGateway"` / `"AccountService")`, `JsonFormatter` console sink, `builder.Host.UseSerilog()`
- [x] Implement: `src/EventLedger.Gateway/Middleware/TraceLoggingMiddleware.cs` (and the Account Service equivalent) — `LogContext.PushProperty("TraceId", Activity.Current?.TraceId.ToString())` middleware, registered via `app.Use(...)` in `Program.cs`. Works today even without OpenTelemetry SDK registered (issue #4) — ASP.NET Core populates `Activity.Current` per request by default; OTel later replaces what populates that activity, not whether one exists.
- [x] Bug found and fixed during TDD green phase: `Console.Out` redirection + Serilog's static `Log.Logger` are process-wide global state, racing under xUnit's default parallel-by-class execution — added `[assembly: CollectionBehavior(DisableTestParallelization = true)]` to both test assemblies. Separately, initial test logic picked the *first* line containing `ServiceName` (present on every line, including pre-request startup logs) rather than a line with `ServiceName` **and** `TraceId` together — fixed the test's line-matching, not just the parallelism.

### Phase 4: Trivial health endpoint (both services)

- [x] Test: `GET /health` → `200` with `{"status": "ok"}` (integration, Phase 4) — confirmed red first (404, no route) before the controller existed
- [x] Implement: `src/EventLedger.Gateway/Controllers/HealthController.cs` — `[HttpGet("/health")] public IActionResult Health() => Ok(new { status = "ok" });`
- [x] Implement: `src/EventLedger.AccountService/Controllers/HealthController.cs` — same

### Phase 5: Gateway configuration for the Account Service base URL

- [ ] Implement: `src/EventLedger.Gateway/appsettings.json` — add an `AccountService:BaseUrl` section (e.g. `"http://localhost:5051"` for local dev) per [architecture/deployment-architecture.md](../../architecture/deployment-architecture.md). No test — nothing consumes this value until issue #2 adds the outbound call; exercised indirectly then.

## Testing Strategy

### Test Environment

xUnit + `Microsoft.AspNetCore.Mvc.Testing`'s `WebApplicationFactory<Program>`. No SQLite involved in this story's tests — no `DbContext` exists yet. Boot and `/health` tests only.

### Test Cases

Listed inline within each phase above, before the implementation step(s) they verify.

## Decisions Made

1. **`dotnet new web`** (not `webapi`) as the `.csproj` baseline — brainstorm Approach 1; avoids both hand-written-SDK-attribute risk and generated-boilerplate cleanup.
2. **Trivial `/health`** (`200 {"status": "ok"}`, no DB check) — brainstorm Q1. **The architecture pre-flight confirmed this is a real, temporary gap against `architecture/observability.md`'s documented contract** (which requires a DB-connectivity diagnostic), not a disagreement with that doc. Deliberate sequencing: issue #5 upgrades the same route in place, not a new one.
3. **OpenTelemetry fully deferred** to issue #4 — brainstorm Q2.
4. **Two xUnit test projects scaffolded now**, empty except one boot-smoke-test each — brainstorm Q3.
5. **Serilog wired up now**, not deferred to #5 — resolved during planning (the brainstorm asked this question for OTel but not, inconsistently, for logging; caught and resolved here). Same "baseline now, refine later" pattern as decision 2. The trace-ID middleware works pre-OTel since it only reads `System.Diagnostics.Activity.Current`, which ASP.NET Core populates by default.
6. **`HealthController` as a proper MVC controller class**, not a minimal-API `app.MapGet` — establishes the same pattern `EventsController`/`AccountsController` (issue #2) will use, keeping `Program.cs` free of inline route mapping.
7. **CancellationToken propagation check**: this story's only async surface is the trivial `/health` action, which has no downstream call to propagate a token to. Not applicable; noted directly rather than asked about.

### Known Constraints

- **`GET /health` does not yet match `architecture/observability.md`'s documented contract.** No DB-connectivity check until issue #5. Anyone reading that doc in isolation before #5 lands would see a spec this story doesn't fully satisfy — that gap is intentional and temporary, not a defect.
- **No `DbContext`, entity, or domain types in this story** — issue #2's Phase 1/2 creates them into the folders this story scaffolds.
- **No OpenTelemetry registration** — issue #4's scope.
- **No Docker Compose/Dockerfiles** — issue #8's scope, even though `appsettings.json` is structured to support it (config-based base URL) from the start.
