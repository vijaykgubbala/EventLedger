---
issue: 5
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/5
branch: 5_observability
base: master
plan: docs/plans/5_observability-plan.md
---

# Handoff: Observability — structured logging, health checks, custom metric

## Release Notes

This PR closes out issue #5's three observability acceptance criteria (OBS-3 through OBS-5), building on the tracing infrastructure from issue #4.

**OBS-3 (structured logging)** turned out to already be fully satisfied by issues #3/#4 — both services already emit JSON log lines with timestamp, level, service name, trace ID, and message. This PR adds no new logging code; it adds a regression test per service (`Request_ProducesJsonLogLineWithTimestampLevelAndMessage`) that explicitly asserts all five required fields in one place, closing the gap between "it happens to work" and "it's verified as this story's acceptance criterion."

**OBS-4 / FB-12 (health checks)** upgrades `GET /health` on both services from issue #3's trivial `{"status":"ok"}` placeholder to a real database-connectivity check via EF Core's `Database.CanConnectAsync()`. The endpoint always returns `200` — the response body's `status`/`database` fields carry the diagnostic (`{"status":"degraded","database":"unreachable"}` when the DB can't be reached) — matching `architecture/observability.md`'s framing of `/health` as "answer from local state only," never a proxy for HTTP-level alerting.

**OBS-5 (custom metric)** adds a request-count-by-endpoint-and-status `Counter<long>` metric, recorded by a new `RequestMetricsMiddleware` per service. The middleware mirrors the existing `TraceLoggingMiddleware`'s shape exactly (a static class wrapping `app.Use(...)`, not a conventional constructor-injected middleware class) and tags each measurement with the matched route *template* (e.g. `accounts/{accountId}/balance`), not the raw request path, to keep the metric's cardinality bounded regardless of how many distinct account or event IDs are ever seen. No metrics exporter/backend is introduced — the counter's value is verified directly in tests via an in-process `System.Diagnostics.Metrics.MeterListener`, keeping issue #4's deliberate no-backend decision consistent.

A `workflow-review` pass on this branch found and fixed two real gaps beyond the original implementation: the metrics middleware silently dropped the counter increment for any request that threw an unhandled exception (undercounting exactly the failure case the metric exists to surface — fixed with explicit exception handling that records a 500 before rethrowing), and both `HealthController`s were injecting their `DbContext` directly, violating this codebase's own Controllers→Application-handler layering rule (fixed by extracting a small `HealthCheckHandler` in each service's `Application/` layer, matching every other endpoint's existing shape). Five smaller documentation-sync and test-cleanup suggestions were also addressed — see `docs/reviews/5_observability.json` for the full disposition record.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| `HealthController` (both services) | Small — one endpoint per service, no other code calls `/health` | Whether the DB-unreachable path is genuinely exercised, not just the happy path | `HealthControllerTests.cs` covers both branches; the Gateway's unreachable-DB test corrupts a real, isolated temp SQLite file after host startup rather than mocking, so it exercises the actual `CanConnectAsync()` failure path |
| `RequestMetricsMiddleware` (new, both services) | Medium — runs on every single request in both services' pipelines | Whether the exception-safety fix is correct (does the counter really still increment when a request fails?), and whether route-template tagging actually bounds cardinality | `RequestThatThrowsUnhandledException_StillRecordsMeasurement` forces a genuine unhandled `SqliteException` (not a mock) and asserts the counter still records with status `500`; `GetEventById_...`/`GetAccountBalance_...` tests assert the tag is the route template, not the ID-bearing raw path |
| OTel SDK registration (`.WithMetrics(...)`, both `ServiceCollectionExtensions.cs`) | Small — additive to existing tracing registration from issue #4 | Whether metrics registration itself is covered by a real regression test, not just inferred from the middleware working | `OpenTelemetryRegistrationTests.cs` extended with `MeterProvider` DI-resolution tests per service, mirroring the existing `TracerProvider` test pattern |
| Documentation (`architecture/observability.md`, `standards/backend-architecture.md`, `standards/logging-dotnet.md`) | Small — docs only | Whether the corrections accurately reflect what shipped | All three were corrected during the `workflow-review` pass in response to specific, cited staleness findings — see `docs/reviews/5_observability.json` (F6–F8) |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| OBS-3: all five required JSON log fields asserted in one place (both services) | written | `Request_ProducesJsonLogLineWithTimestampLevelAndMessage`, both `*LoggingTests.cs` — passed immediately, confirming the fields already existed |
| OBS-4: `/health` returns `200` with `{"status":"ok","database":"ok"}` when reachable (both services) | written | `GetHealth_DatabaseReachable_Returns200WithOkStatus` |
| OBS-4: `/health` returns `200` with `{"status":"degraded","database":"unreachable"}` when unreachable (Gateway) | written | `GetHealth_DatabaseUnreachable_Returns200WithDegradedStatus` — the only genuinely new behavior in Phase 2 |
| OBS-5: `MeterProvider` resolvable via DI (both services) | written | Mirrors the existing `TracerProvider` registration test from issue #4 |
| OBS-5: real request produces one `Counter` measurement tagged with route template + status (both services) | written | `RequestMetricsMiddlewareTests.cs`, both a no-parameter route (`/health`) and a parameterized one (`events/{eventId}` / `accounts/{accountId}/balance`) to actually exercise the route-template-vs-raw-path decision |
| (unplanned) Unhandled exception still records the metric measurement (both services) | added | `RequestThatThrowsUnhandledException_StillRecordsMeasurement` — added during `workflow-review` after F1/F2 found the original middleware silently dropped failed requests |

### What's Not Tested

The Account Service's `/health` DB-unreachable path has no dedicated test (only the Gateway's does) — not a planned gap, just not mandated by OBS-4's acceptance criteria, which don't distinguish per-service coverage depth; the underlying `HealthCheckHandler`/`CanConnectAsync` logic is identical between services and the Gateway's test already exercises that code path. The OTel resource `service.name` attribute (a single, directly-inspectable `ConfigureResource(...)` line, unchanged since issue #4) remains untested — asserting it would require standing up an in-memory span exporter solely for one test, judged disproportionate for this project's scope during issue #4's review and not revisited here since nothing about it changed.
