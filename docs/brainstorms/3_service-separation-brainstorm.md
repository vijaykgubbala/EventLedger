# Brainstorm: Service Separation (independent processes, independent SQLite databases)

**Date:** 2026-07-15
**Issue:** #3

## Problem Statement

Stand up `EventLedger.Gateway` and `EventLedger.AccountService` as two
independently runnable ASP.NET Core projects, each with its own SQLite
database, following the folder-based layering and file-placement rules
already fully specified in `standards/backend-architecture.md`. This is
the first story to produce actual application code — everything before
this point in the repo is documentation and tooling. This is
[issue #3](https://github.com/vijaykgubbala/EventLedger/issues/3),
milestone "Phase 1 - Core Domain & Service Separation," and it's the
first-ordered story in the dependency chain (nothing else depends on
anything upstream of it).

## Codebase Context

The scaffold this story produces is not open for design — it's already
fully specified. Verbatim from research:

- **Exact repository tree** (`standards/backend-architecture.md`):
  `src/EventLedger.Gateway/{Controllers,Application,Domain,Infrastructure,Middleware}`,
  `src/EventLedger.AccountService/{same}`,
  `tests/EventLedger.Gateway.Tests/`, `tests/EventLedger.AccountService.Tests/`,
  `EventLedger.sln` at the root. Single project per service — explicitly
  not a multi-assembly split.
- **Namespaces**: `EventLedger.Gateway.*` / `EventLedger.AccountService.*`
  — [standards/naming.md](../../standards/naming.md).
- **Serilog setup** has an exact, already-written snippet (JSON console
  sink, `Enrich.FromLogContext()` required, `ServiceName` property) plus
  the trace-ID `LogContext.PushProperty` middleware — both go in
  `Program.cs`/`Middleware/` per
  [standards/logging-dotnet.md](../../standards/logging-dotnet.md).
- **SQLite**: file-based, `EnsureCreated()` (not Migrations), one file
  per service, no `:memory:`/`InMemory` anywhere — but the actual
  `DbContext` classes belong to issue #2, not this story (see below).
- **Account Service base URL** on the Gateway must come from
  configuration (`appsettings.json`/env var), never hardcoded — per
  [architecture/deployment-architecture.md](../../architecture/deployment-architecture.md)
  — even though Docker Compose itself is issue #8.
- **Service boundaries**: the Account Service must remain "callable and
  testable with zero outbound HTTP dependencies"; neither service shares
  a database, connection string, or schema —
  [standards/service-boundaries.md](../../standards/service-boundaries.md).

**Explicit hand-off from issue #2's plan** (already written): its
Context section states this plan "assumes issue #3 ... has already been
executed — the `.csproj` files, `Program.cs` skeletons, and DI container
exist," and its Phase 2 explicitly notes `Database.EnsureCreated()`
"the file itself is owned by issue #3" — i.e. #3 creates `Program.cs`
and the call site; #2 adds the `EnsureCreated()` line and `DbContext`
registration into it. **This story must not create `DbContext` classes,
entity types, or domain types (`Event`, `Transaction`, `TransactionType`)
— those are issue #2's Phase 1/2.**

**Scope tension found in research**: issue #3's own acceptance criteria
(DEP-1/DEP-2) mention `GET /health`, but the full `/health` spec (DB
connectivity diagnostic) is documented in
[architecture/observability.md](../../architecture/observability.md) and
explicitly deferred to issues #4/#5 in issue #2's plan's Known
Constraints. Resolved in Q&A below.

**Existing convention already binding on this story**:
`.claude/skills/workflow-execute/SKILL.md`'s "ASP.NET Core project
conventions" section (written before this story ran) already mandates
`Program.cs` be an orchestrator only, with DI/middleware wiring in
`Infrastructure/` extension methods, and a `public partial class Program`
marker for `WebApplicationFactory<Program>` testability — this isn't an
open choice for this brainstorm, it's a standing rule this story must
follow from its first commit.

## Q&A Decisions

**Q1: DEP-1/DEP-2 reference `GET /health`, but the full spec is deferred to #4/#5. How should this story handle it?**
A: Add a trivial placeholder — `GET /health` returns `200` with `{"status": "ok"}`, no DB check yet. Issue #5 upgrades the same route with the DB-connectivity diagnostic later, not a new route.

**Q2: Should `Program.cs` register OpenTelemetry now, since it's a `Program.cs` concern and this story creates that file?**
A: No — defer entirely to issue #4. It adds the full `AddOpenTelemetry()` registration in one piece rather than this story adding a partial registration #4 has to modify.

**Q3: Should this story also scaffold the two xUnit test projects, empty but buildable?**
A: Yes — both `.csproj` files, project references, xUnit + `Microsoft.AspNetCore.Mvc.Testing` package refs, added to the `.sln`. This also gives DEP-1/DEP-2 something concrete to verify against: a `WebApplicationFactory`-based boot-smoke-test per service, not just a manual `dotnet run` check.

## Proposed Approaches

### Approach 1: `dotnet new web` baseline + hand-written orchestrator `Program.cs`

Use the ASP.NET Core **`web`** template (not `webapi` — `web` generates
only `Program.cs` + `appsettings.json`, no `WeatherForecastController`,
no Swagger scaffolding to delete) to get a correct, known-good
`.csproj`/SDK baseline per service, then hand-write everything else: a
near-empty `AddGatewayInfrastructure()` / `AddAccountServiceInfrastructure()`
extension method in `Infrastructure/` (following the orchestrator rule
above — even though there's almost nothing to register yet, this is
where #2/#4/#6 will each add one call), a trivial `HealthController` in
`Controllers/`, the Serilog snippet + trace-ID middleware in
`Middleware/`, and an `AppMarker.cs` with `public partial class Program`
added proactively (cheap now, avoids a "test project won't compile"
surprise when #2's integration tests need `WebApplicationFactory<Program>`).

**Pros:**
- `dotnet new web` removes any risk of hand-typing the SDK-style
  `.csproj` attributes wrong (target framework, implicit usings,
  nullable context) for zero cleanup cost, since the minimal template
  generates no boilerplate controllers or Swagger config to delete.
- `Program.cs` shape is correct from commit one — #2, #4, and #6 each
  add one line to an existing extension method rather than needing to
  first refactor `Program.cs` into the orchestrator shape themselves.
- `AppMarker.cs` existing from the start means issue #2's integration
  tests (Phase 5/6) and issue #9's cross-service test never hit a
  surprise compile failure.

**Cons:**
- The `Infrastructure/` extension methods start out doing nothing
  (`AddGatewayInfrastructure` just returns the builder unchanged) — a
  small stub that looks like premature abstraction in isolation, though
  it's directly required by an already-standing rule
  (`workflow-execute/SKILL.md`), not a new one invented here.

### Approach 2: Fully hand-written `.csproj` files, no `dotnet` CLI scaffolding

Write every `.csproj` line by hand (`TargetFramework`, `ImplicitUsings`,
`Nullable`, package references) with no generated starting point.

**Pros:**
- Guarantees literally nothing unintended in the file — maximum control.

**Cons:**
- Trades a small amount of "hand-crafted purity" for real risk: getting
  SDK-style project attributes exactly right by hand is more error-prone
  than trusting `dotnet new web`'s known-correct output, for no actual
  benefit — the `web` template doesn't generate meaningful cruft to clean
  up the way `webapi` would. This is risk with no offsetting reward.

## Recommendation

**Approach 1.** `dotnet new web` (not `webapi`) is the right amount of
tooling — it removes a real class of csproj mistakes without introducing
any cleanup burden, since the minimal template has nothing to clean up.
Everything else (folder layout, namespaces, the orchestrator `Program.cs`
pattern, the Serilog/trace-ID middleware, `AppMarker.cs`, the trivial
`/health`) is already dictated by standing docs or standing rules — this
brainstorm mostly confirmed there's no real design choice left to make at
the approach level for a story this mechanical, beyond the three Q&A
decisions above.

## Related Docs

- [docs/plans/2_core-functionality-plan.md](../plans/2_core-functionality-plan.md) — the downstream plan whose Phase 1/2 depend on this story's scaffold matching exactly.
- [standards/backend-architecture.md](../../standards/backend-architecture.md), [standards/service-boundaries.md](../../standards/service-boundaries.md), [standards/logging-dotnet.md](../../standards/logging-dotnet.md), [architecture/observability.md](../../architecture/observability.md), [architecture/deployment-architecture.md](../../architecture/deployment-architecture.md) — all directly load-bearing on this story.
- Next: `workflow-plan 3`.
