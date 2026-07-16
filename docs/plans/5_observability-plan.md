# Observability — structured logging, health checks, custom metric

**Issue:** #5

## Context

This plan implements issue #5's three acceptance criteria on top of issues #2–#4 (already merged to `master`): OBS-3 (structured JSON logging — already implemented, needs verification), OBS-4/FB-12 (`GET /health` DB-connectivity check, upgrading the trivial placeholder from issue #3), and OBS-5 (a custom OpenTelemetry `Counter` metric for request count by endpoint+status). See [docs/brainstorms/5_observability-brainstorm.md](../brainstorms/5_observability-brainstorm.md) for the full research, Q&A, and rejected alternatives (relying on `AddAspNetCoreInstrumentation()`'s built-in metrics instead of a custom `Counter`; a Console exporter instead of an in-memory test assertion; raw request path instead of route template as the metric's endpoint tag).

`architecture/observability.md` already fully specifies the target design for all three criteria — this is implementation of a settled decision, confirmed via architecture pre-flight (no conflicts found; see Decisions Made).

## Relevant Learnings

- [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md) — `HealthController`'s new `CanConnectAsync` call must thread a `CancellationToken` parameter from the action method straight through, per this codebase's established convention.
- Issue #4's `workflow-review` finding F2 (critical): neither of that story's new tests would have failed if the actual OTel SDK registration were deleted — closed by adding a dedicated DI-resolution registration test (`OpenTelemetryRegistrationTests.cs`, both services). The same gap applies here: an in-memory `MeterListener` test alone proves the counter fires, but not that `.WithMetrics(...)` is actually registered in DI. This plan proactively includes the equivalent `MeterProvider`-registration test rather than waiting for a review pass to catch the gap a second time.
- No `docs/solutions/` entries exist yet (expected — early project).

## Architecture Pre-Flight Finding

No conflict. The `architecture-advisor` agent confirmed both the health-check design (DB-only, no cross-service call, always answers from local state — matching `architecture/observability.md`'s anti-pattern "Do not make `GET /health` call the other service or otherwise block on a network round-trip") and the metric design (Counter incremented in middleware, no exporter/backend — matching the anti-pattern "Do not introduce a metrics backend... as a requirement") are direct implementations of the already-recorded contract. No `architecture/*.md` edits are needed for this story.

## Implementation Steps

### Phase 1: OBS-3 — structured logging regression test (no new `src/` code)

Confirmed via research: both services' `BootstrapLogging(serviceName)` (`src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs:11-18`, AccountService twin) already configure `Enrich.FromLogContext()`, `Enrich.WithProperty("ServiceName", serviceName)`, and `WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())`; `TraceLoggingMiddleware` already pushes `TraceId`. Serilog's (non-compact) `JsonFormatter` emits `Timestamp`, `Level`, and `MessageTemplate` as top-level fields on every line regardless of level (confirmed empirically against captured log output earlier this session — `Level` is not omitted for `Information`, unlike `CompactJsonFormatter`). So all five OBS-3 fields already exist; this phase only adds the missing verification.

- [x] Write test: extend `tests/EventLedger.Gateway.Tests/GatewayLoggingTests.cs`'s existing `Request_ProducesJsonLogLineWithServiceNameAndTraceId` test (or add a sibling test in the same file) to assert all five OBS-3 fields explicitly in one place: `Timestamp` present and parses as a valid date, `Level` present (e.g. `"Information"`), `MessageTemplate` (or rendered `Message`, whichever Serilog's `JsonFormatter` actually names it — confirm against a captured line before writing the assertion) non-empty, `ServiceName` correct (already asserted), `TraceId` well-formed (already asserted). Added as a sibling test, `Request_ProducesJsonLogLineWithTimestampLevelAndMessage`; confirmed field name is `MessageTemplate` (Serilog's `JsonFormatter` with default `renderMessage: false`).
- [x] Write test: mirror the same extension in `tests/EventLedger.AccountService.Tests/AccountServiceLoggingTests.cs`.
- [x] Confirm both tests pass unchanged against the existing implementation — no `src/` changes expected in this phase. If either genuinely fails, that's new information contradicting this plan's Phase 1 premise; stop and investigate before continuing (per this project's TDD discipline — don't paper over a genuine gap). Both passed immediately, confirming the premise.

### Phase 2: OBS-4/FB-12 — `/health` DB-connectivity check (both services)

- [x] Write test: update `tests/EventLedger.Gateway.Tests/HealthControllerTests.cs`'s existing exact-body-string assertion (`Assert.Equal("{\"status\":\"ok\"}", body)`) to match the new shape (`{"status":"ok","database":"ok"}`), and confirm the response is still `200 OK`.
- [x] Write test: same update in `tests/EventLedger.AccountService.Tests/HealthControllerTests.cs`.
- [x] Write test: a DB-unreachable case for at least one service (e.g. Gateway) — construct a `WebApplicationFactory<Program>` whose `GatewayDbContext` is reconfigured (via `WithWebHostBuilder`, following the `ConfigureGatewayDb`-style pattern already used in `GatewayToAccountServiceFullFlowTests.cs`) to point at an unreachable SQLite target (e.g. a nonexistent directory in the connection string), then assert `GET /health` still returns `200` with `{"status":"degraded","database":"unreachable"}`. **Deviation from the plan's literal approach**: a nonexistent directory *at startup* crashes the whole host, because `Program.cs` calls `EnsureGatewayDatabaseCreated()` (`Database.EnsureCreated()`) during boot — before `/health` is ever reached. Fixed by creating a real, valid temp file path so startup succeeds, forcing host startup via `factory.Services`, then deleting the file and clearing SQLite's connection pool *afterward*, isolating the failure to the `/health` request itself. Simplified from an initial directory-based version (`Directory.CreateDirectory`/`Directory.Delete(recursive: true)`) to a plain file delete during `/simplify` — confirmed reliable across 5 runs; WAL-mode sidecar files (`-wal`/`-shm`, left over from `EnsureCreated()`'s `PRAGMA journal_mode = 'wal'`) referencing a since-deleted main file are the likely reason a missing *file* (unlike a missing *directory*) still fails to reopen rather than being silently recreated.
- [x] Implement: `src/EventLedger.Gateway/Controllers/HealthController.cs` — inject `GatewayDbContext` via constructor, change `Health()` to `async Task<IActionResult> Health(CancellationToken cancellationToken)`, call `await _db.Database.CanConnectAsync(cancellationToken)`, return `Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" })`. Always `200` — never a non-2xx status for this endpoint, per the brainstorm's Q2 decision and the architecture doc's "answers from local state only" framing.
- [x] Implement: same change in `src/EventLedger.AccountService/Controllers/HealthController.cs` with `AccountDbContext`.
- [x] Confirm both new tests pass and the existing (now-updated) body-shape tests pass. Full solution: 70/70 passing (25 Account Service + 45 Gateway).

### Phase 3: OBS-5 — custom request-count metric (both services)

Confirmed via research: `TraceLoggingMiddleware` (`src/EventLedger.Gateway/Middleware/TraceLoggingMiddleware.cs`, AccountService twin) is a **static class with an `app.Use((context, next) => ...)` lambda inside an extension method** (`UseTraceLogging()`), not a conventional constructor-injected `InvokeAsync` middleware class. `RequestMetricsMiddleware`/`UseRequestMetrics()` must follow the exact same shape for consistency with this codebase's only existing middleware precedent. `.WithMetrics(...)` chains onto the same `AddOpenTelemetry()` builder already present in both `ServiceCollectionExtensions.cs` files, alongside the existing `.WithTracing(...)` call — no new NuGet package needed (`OpenTelemetry.Extensions.Hosting` 1.16.0, already referenced by both services, provides `WithMetrics`; `System.Diagnostics.Metrics.Counter<T>`/`MeterListener` are BCL).

- [x] Write test: `tests/EventLedger.Gateway.Tests/OpenTelemetryRegistrationTests.cs` — add a sibling test (or extend the existing file) asserting `factory.Services.GetService<MeterProvider>()` (from `OpenTelemetry.Metrics`) is non-null, mirroring the existing `TracerProvider` test exactly. This closes the same regression-coverage gap issue #4's review found (F2) — proves `.WithMetrics(...)` registration itself is covered, independent of the counter-value test below.
- [x] Write test: same in `tests/EventLedger.AccountService.Tests/OpenTelemetryRegistrationTests.cs`.
- [x] Write test: a new test file (e.g. `tests/EventLedger.Gateway.Tests/RequestMetricsMiddlewareTests.cs`) that constructs a `System.Diagnostics.Metrics.MeterListener`, subscribes to the Gateway's named `Meter` (`SetMeasurementEventCallback<long>` recording into a list), enables listening for instruments matching that `Meter`'s name, drives one real request through `WebApplicationFactory<Program>.CreateClient()` (e.g. `GET /health`, cheapest existing endpoint), and asserts exactly one `Counter<long>` measurement was recorded with value `1` and tags `endpoint` = the route template (`/health`) and `status_code` = `200`. Call `meterListener.RecordObservableInstruments()`/dispose appropriately per `MeterListener`'s documented usage to avoid leaking the subscription across tests. **Deviation**: `RoutePattern.RawText` never carries a leading slash regardless of how the route attribute was written — asserted `"health"`, not `"/health"` (discovered empirically, not assumed).
- [x] Write test: same in `tests/EventLedger.AccountService.Tests/`, targeting `GET /health` on that service.
- [x] Write test: a second request in one of the two tests above (e.g. `POST /events` on the Gateway, following `EventsControllerTests.cs`'s existing request-building pattern) asserting the `endpoint` tag reads the route template (`/events`), not a raw path — this is what actually exercises the Q6 cardinality decision, since `/health` alone wouldn't distinguish a route-template implementation from a raw-path one. **Deviation**: used `GET /events/{eventId}` (Gateway) and `GET /accounts/{accountId}/balance` (Account Service) instead of `POST /events` — both have a real route parameter and need no Account Service stub/network dependency, keeping the test focused purely on the metrics middleware rather than on `EventsControllerTests.cs`'s private `StubAccountServiceHandler`. Also surfaced and cleaned up an unrelated stale `gateway.db` build artifact (pre-existing schema drift in a reused test-output file, not a regression from this change) that caused a spurious 500 on first run.
- [x] Implement: `src/EventLedger.Gateway/Middleware/RequestMetricsMiddleware.cs` — static class, `UseRequestMetrics()` extension method on `IApplicationBuilder`, following `TraceLoggingMiddleware`'s exact shape. A static, module-level `Meter` (e.g. `new Meter("EventLedger.Gateway")`) and `Counter<long>` (e.g. `meter.CreateCounter<long>("http.requests.total")`) — module-level `static readonly` fields, since `Meter`/`Counter` instances are meant to be long-lived per this codebase's existing pattern of static/singleton cross-cutting infrastructure (matching how `TraceLoggingMiddleware` itself is a static class). Inside the `app.Use((context, next) => ...)` lambda, after `await next()`: read the route template via `(context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText ?? "unknown"`, read `context.Response.StatusCode`, and call `counter.Add(1, new KeyValuePair<string, object?>("endpoint", routeTemplate), new KeyValuePair<string, object?>("status_code", statusCode))`. Meter name exposed as a `public const string MeterName` so `ServiceCollectionExtensions.cs`'s `.WithMetrics(...)` call references it directly instead of duplicating the literal.
- [x] Implement: same in `src/EventLedger.AccountService/Middleware/RequestMetricsMiddleware.cs`, Meter name `"EventLedger.AccountService"`.
- [x] Implement: `src/EventLedger.Gateway/Program.cs` — add `app.UseRequestMetrics();` immediately after the existing `app.UseTraceLogging();` (line 19), before `app.MapControllers();`. Order between the two middleware doesn't matter functionally (neither depends on the other's side effects), so this is arbitrary but kept consistent across both services.
- [x] Implement: same in `src/EventLedger.AccountService/Program.cs`.
- [x] Implement: `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` — add `.WithMetrics(metrics => metrics.AddMeter(RequestMetricsMiddleware.MeterName))` to the existing `AddOpenTelemetry()` chain, alongside `.WithTracing(...)`.
- [x] Implement: same in `src/EventLedger.AccountService/Infrastructure/ServiceCollectionExtensions.cs`, meter name `"EventLedger.AccountService"`.
- [x] Confirm all new tests pass (registration tests + `MeterListener` value tests), and existing tests (`GatewayToAccountServiceFullFlowTests.cs`, `EventsControllerTests.cs`, etc.) remain green — the new middleware sits in every request's pipeline, so a regression here would show up broadly. Full solution: 76/76 passing (28 Account Service + 48 Gateway).

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../../standards/backend-architecture.md#test-project-layout). `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is already present in both test projects (from issue #3, for `Console.Out`/static-`Log.Logger` reasons) — the new `MeterListener`-based tests don't share that specific hazard (a `Meter` is scoped to its own name, not a shared static like `Log.Logger`), but running in the same serialized suite is harmless and requires no new configuration. The DB-unreachable health-check test needs a real (but intentionally broken) SQLite target, not `InMemory` — consistent with [docs/patterns/2026-07-15-idempotency-key-race.md](../../../docs/patterns/2026-07-15-idempotency-key-race.md)'s "real file-based SQLite" rule, though this test is checking failure behavior rather than idempotency specifically.

### Test Cases

- **Description**: A captured Gateway log line contains all five OBS-3-required fields (timestamp, level, service name, trace ID, message), not just the two currently asserted.
  **Type**: Integration (`WebApplicationFactory<Program>`, `Console.Out` capture via the existing `ConsoleLogCapture` helper).
  **Edge cases**: N/A — this is verifying already-implemented behavior, not new logic.
  **Phase reference**: Phase 1.
- **Description** (same, Account Service): identical assertion shape in `AccountServiceLoggingTests.cs`.
  **Type**: Integration.
  **Phase reference**: Phase 1.
- **Description**: `GET /health` returns `200` with `{"status":"ok","database":"ok"}` when the database is reachable (both services).
  **Type**: Integration (`WebApplicationFactory<Program>`).
  **Edge cases**: Updates the existing exact-string assertion from issue #3's placeholder shape.
  **Phase reference**: Phase 2.
- **Description**: `GET /health` returns `200` with `{"status":"degraded","database":"unreachable"}` when the database is unreachable, on at least one service.
  **Type**: Integration (`WebApplicationFactory<Program>` with a deliberately-broken `DbContext` connection string via `WithWebHostBuilder`).
  **Edge cases**: This is the only genuinely new behavior in Phase 2 — the happy path was already implicitly covered by every other integration test that hits a working DB.
  **Phase reference**: Phase 2.
- **Description**: `MeterProvider` is resolvable via DI on both services once `.WithMetrics(...)` is registered.
  **Type**: Integration (DI resolution, mirrors `OpenTelemetryRegistrationTests.cs`'s existing `TracerProvider` test).
  **Edge cases**: None — binary registered/not-registered check.
  **Phase reference**: Phase 3.
- **Description**: A real HTTP request through a real `WebApplicationFactory` produces exactly one `Counter<long>` measurement of value `1`, tagged with the correct route template and status code — proving the metric is both wired and observably changes with traffic (OBS-5's literal acceptance criterion).
  **Type**: Integration (`WebApplicationFactory<Program>` + `System.Diagnostics.Metrics.MeterListener`).
  **Edge cases**: A second test case asserting the `endpoint` tag is the route template (e.g. `/events`), not a raw path — the only test that actually exercises the Q6 cardinality decision.
  **Phase reference**: Phase 3.
- **Description** (Account Service): same shape as the two above, targeting `GET /health` on that service.
  **Type**: Integration.
  **Phase reference**: Phase 3.

## Decisions Made

1. **OBS-3 requires no new `src/` code** — all five required fields are already implemented from issues #3/#4; this story's logging work is regression-test coverage only (brainstorm Q1).
2. **`/health` always returns `200`**; the response body's `status`/`database` fields carry the DB-connectivity diagnostic (brainstorm Q2). No non-2xx status is ever returned from this endpoint.
3. **DB check via `Database.CanConnectAsync(cancellationToken)`** — EF Core's built-in API, consistent with this codebase having no raw ADO.NET/SQL anywhere in `src/` (brainstorm Q3).
4. **OBS-5 uses a hand-created `Counter<long>` + dedicated middleware**, not `AddAspNetCoreInstrumentation()`'s built-in metrics — matches `architecture/observability.md`'s literal "Counter... incremented in middleware" wording (brainstorm Q4).
5. **No exporter/metrics backend** — the counter's value is verified via an in-process `System.Diagnostics.Metrics.MeterListener` in tests, keeping issue #4's no-backend decision consistent (brainstorm Q5).
6. **Metric's `endpoint` tag is the route template, not the raw request path** — bounds cardinality regardless of how many distinct `accountId` values are ever seen on `/accounts/{accountId}/transactions` (brainstorm Q6).
7. **`RequestMetricsMiddleware` mirrors `TraceLoggingMiddleware`'s exact shape** (static class, `app.Use()` lambda inside an extension method, not a conventional constructor-injected middleware class) — confirmed via codebase research to be this project's only existing middleware precedent; matching it exactly rather than introducing a second middleware convention.
8. **A `MeterProvider` DI-registration test is included proactively**, not just the `MeterListener` value test — applies the lesson from issue #4's `workflow-review` finding F2 (neither of that story's original tests would have failed if the actual OTel registration were deleted) before a review pass has to catch the same gap a second time.
9. **No `architecture/*.md` or `standards/*.md` edits needed** — confirmed via architecture pre-flight (`architecture-advisor` agent found no conflict; both the health-check and metric designs are direct implementations of the already-recorded `observability.md` contract).

### Known Constraints

- Latency histograms and error-rate metrics are explicitly out of scope (`architecture/observability.md` frames these as bonus scope beyond the one required Counter metric) — this plan implements request-count-by-endpoint-and-status only.
- The DB-unreachable test scenario for Phase 2 is implemented for at least one service (Gateway); mirroring it for the Account Service is not required by OBS-4's acceptance criteria (which don't distinguish per-service coverage depth) but may be added if time permits during execution — not a planned gap, just not mandated.
