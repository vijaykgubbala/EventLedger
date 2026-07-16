# Brainstorm: Observability — structured logging, health checks, custom metric

**Date:** 2026-07-16
**Issue:** #5

## Problem Statement

Issue #5 ("Story 4: Observability") has three acceptance criteria:

- **OBS-3**: every log line is JSON with timestamp, level, service name, trace ID, message.
- **OBS-4 / FB-12**: `GET /health` returns `200` with status + a DB-connectivity check, on both services.
- **OBS-5**: at least one metric (request count by endpoint + status) recorded and observably changes with traffic.
- `Enrich.FromLogContext()` configured on both services (not optional).

## Codebase Context

Issues #2 (Core functionality), #3 (Service separation), and #4 (Distributed tracing) are already implemented and merged to `master`. Relevant existing state:

**Structured logging (OBS-3) already appears fully implemented.** Both services' `BootstrapLogging(serviceName)` (`src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs:11-18`, `src/EventLedger.AccountService/Infrastructure/ServiceCollectionExtensions.cs:11-18`) already configure:
```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", serviceName)
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();
```
`TraceLoggingMiddleware` (added in issue #3, both services) already pushes `TraceId` into `LogContext`. Serilog's `JsonFormatter` emits `@t`/`@l`/`@m` automatically. `GatewayLoggingTests.cs`/`AccountServiceLoggingTests.cs` already assert `ServiceName` and `TraceId` format, but not framed as one explicit OBS-3 acceptance test covering all five required fields together.

**Health checks (OBS-4) are still the trivial placeholder from issue #3**, by design — `architecture/observability.md` explicitly notes issue #3 shipped `{"status":"ok"}` as a deliberate placeholder and that "issue #5 upgrades the same route in place to match the full contract." Both `HealthController.cs` files (`src/EventLedger.Gateway/Controllers/HealthController.cs:8-9`, `src/EventLedger.AccountService/Controllers/HealthController.cs:8-9`) currently just return `Ok(new { status = "ok" })`, no `DbContext` injected. `architecture/observability.md` is explicit that `/health` must "answer quickly from local state" and the Gateway's `/health` must never block on Account Service reachability.

**Metrics (OBS-5) don't exist at all yet.** `architecture/observability.md`'s custom-metric section: "Each service tracks request count by endpoint and status code as an OpenTelemetry `Counter` metric, incremented in middleware on every request." Both services already call `AddOpenTelemetry()...WithTracing(...)` (issue #4) in `Infrastructure/ServiceCollectionExtensions.cs` — a `.WithMetrics(...)` call belongs alongside it on the same builder chain. `standards/backend-architecture.md`'s cross-cutting-concerns table already earmarks metrics registration for issue #5 in the same `Infrastructure/` location as tracing (row text: "metrics is issue #5's scope"). No metrics-specific NuGet package exists yet; `OpenTelemetry.Instrumentation.AspNetCore` (already referenced by both services since issue #4) provides built-in framework request metrics for free but not the specific custom `Counter` the architecture doc asks for. No exporter is registered on either service (deliberate — issue #4 Decision 2 says a real metrics/tracing backend is unnecessary for this local, solo-run system); `architecture/observability.md`'s anti-patterns explicitly forbid introducing one (Prometheus/Grafana).

**Relevant patterns**: [docs/patterns/2026-07-16-webapplicationfactory-forces-testserver.md](../patterns/2026-07-16-webapplicationfactory-forces-testserver.md) and [docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md](../patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md) — both about `WebApplicationFactory`/real-transport testing gotchas from issue #4; not directly applicable here since metrics testing (per the Q&A below) uses in-process `MeterListener`, not real sockets, but worth keeping in mind if any test needs a genuine request/response cycle.

## Q&A Decisions

**Q1: OBS-3's field list already appears fully implemented from issues #3/#4 — what's issue #5's actual logging-scope work?**
A: Just add regression tests. No new `src/` logging code — extend or add a test that explicitly asserts every OBS-3-required field is present in one place, closing the gap between "it happens to work" and "it's verified as this story's acceptance criterion."

**Q2: What should `/health`'s response look like when the DB check fails?**
A: `200` always; the body's `status`/`database` fields reflect the DB check result (e.g. `{"status":"degraded","database":"unreachable"}`). Matches `architecture/observability.md`'s framing of `/health` as "is this process alive and can it reach its own local state" — a 200 confirms the process itself, the body carries the diagnostic.

**Q3: How should the DB-connectivity check itself be implemented?**
A: `DbContext.Database.CanConnectAsync(cancellationToken)` — EF Core's built-in check, consistent with this codebase's EF-Core-only data access (no raw ADO.NET/SQL anywhere in `src/`).

**Q4: `architecture/observability.md` specifies a Counter "incremented in middleware" — custom instrument, or rely on `AddAspNetCoreInstrumentation()`'s built-in metrics?**
A: Custom `Counter<long>` + a small dedicated middleware component, matching the architecture doc's literal wording and mirroring `TraceLoggingMiddleware`'s existing shape (one focused middleware, registered once, reading the final status code after `await _next(context)`).

**Q5: With no exporter/backend, how should the counter's value actually be "observed" (OBS-5)?**
A: In-memory test assertion only, via `System.Diagnostics.Metrics.MeterListener` (BCL, no new package) — a test subscribes to the service's `Meter`, drives one or more requests through `WebApplicationFactory`, and asserts the `Counter` recorded an increment with the expected tags. Keeps issue #4's no-exporter/no-backend decision consistent, and mirrors how `OpenTelemetryRegistrationTests.cs` already proves `TracerProvider` registration via DI resolution rather than watching console output.

**Q6: The Account Service's route has a parameter (`/accounts/{accountId}/transactions`) — what should the metric's `endpoint` tag record?**
A: The route template (`/accounts/{accountId}/transactions`), not the raw request path — avoids unbounded tag cardinality as new account IDs appear. Read via `(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText`, available after `UseRouting()` has run (same middleware position as `TraceLoggingMiddleware`).

## Proposed Approaches

The logging, health-check, and metric-implementation-shape decisions are already settled by the Q&A above. The one remaining architectural choice is **where the metric-counting logic lives**.

### Approach 1: Dedicated `RequestMetricsMiddleware`, separate from `TraceLoggingMiddleware`

A new middleware class per service (mirroring `TraceLoggingMiddleware`'s existing per-service duplication, since the two services don't share code), registered via its own `app.UseRequestMetrics()` call in `Program.cs` alongside the existing `app.UseTraceLogging()`. It owns exactly one responsibility: read the route template and final status code after `await _next(context)`, and record one `Counter<long>` measurement.

**Pros:**
- Single-responsibility: `TraceLoggingMiddleware` stays about logging/tracing only, this one is about metrics only — consistent with this codebase's existing small, focused-component style (validators, handlers, middleware each do one thing).
- Each is independently testable without the other's concerns bleeding in.
- Matches `Program.cs`'s existing shape (a short, flat list of `app.UseX()` calls) — adding one more line doesn't change that shape's character, just extends the same pattern.

**Cons:**
- Two middleware components both read the response status code in their post-`await` continuation — minor duplication of "where in the pipeline do I read the final status," though each does something different with it.
- One more file per service, one more DI-adjacent registration to remember.

### Approach 2: Fold the counter increment into `TraceLoggingMiddleware` directly

Extend the existing `TraceLoggingMiddleware` to also record the metric in the same post-`await` block where it already reads the final status code for its trace-logging work.

**Pros:**
- No new middleware class or registration — one less moving part.
- Avoids two components reading `context.Response.StatusCode` in slightly different places.

**Cons:**
- Mixes two genuinely distinct concerns (structured trace logging vs. metrics recording) into one class, which this codebase has otherwise avoided (e.g. `EventValidator`, `SubmitEventHandler`, and `EventsController` stay separate rather than merging validation+business logic+HTTP concerns).
- Makes future changes to either concern (e.g. adding a second metric, or changing what gets logged) more likely to require touching a class doing two jobs, increasing the chance of an unrelated regression.
- Harder to test the metric in isolation from logging behavior (or vice versa) without exercising both.

## Recommendation

**Approach 1** — a dedicated `RequestMetricsMiddleware` per service. This follows the same single-responsibility precedent already established by every other component in this codebase (Q4/Q6 already committed to a purpose-built `Counter` + middleware shape matching the architecture doc's literal spec; Approach 1 is the natural continuation of that choice — a dedicated component for a dedicated concern, rather than retrofitting an existing one). The minor duplication cost (two middleware reading the final status code) is small and precedented — `TraceLoggingMiddleware` and this new middleware each already need to run after routing/response-writing for their own, independent reasons.

## Related Docs

- [architecture/observability.md](../../architecture/observability.md) — owning design doc for all three of this story's acceptance criteria.
- [standards/logging-dotnet.md](../../standards/logging-dotnet.md) — Serilog config and required-fields table (OBS-3).
- [standards/backend-architecture.md](../../standards/backend-architecture.md) — cross-cutting-concerns table, already earmarks OTel metrics registration for this story.
- [docs/plans/4_distributed-tracing-plan.md](4_distributed-tracing-plan.md) — prior story's plan; issue #5 layers directly onto its `AddOpenTelemetry()` registration.
