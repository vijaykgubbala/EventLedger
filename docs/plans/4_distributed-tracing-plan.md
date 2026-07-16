# Distributed Tracing — OpenTelemetry, traceparent propagation

**Issue:** #4

## Context

This plan implements OpenTelemetry SDK registration on both services so that a W3C `traceparent` generated at the Gateway is automatically propagated to the Account Service on the one outbound HTTP call, and both services' existing Serilog JSON logs carry a matching `TraceId` for a single request. See [docs/brainstorms/4_distributed-tracing-brainstorm.md](../brainstorms/4_distributed-tracing-brainstorm.md) for the full research, Q&A, and rejected alternatives (bare-BCL propagation without the OTel SDK; hand-rolled `traceparent` header code).

`architecture/observability.md` already fully specifies the target design — this is implementation of a settled decision, not new design. `standards/logging-dotnet.md`'s `TraceLoggingMiddleware` snippet (already implemented verbatim in both services since issue #3) already reads `Activity.Current?.TraceId` and needs **zero code changes** — this story only changes what populates that `Activity`.

## Relevant Learnings

No `docs/solutions/` entries exist yet (expected — this is an early story in the project). Relevant `docs/patterns/` entries: none directly about tracing/OpenTelemetry yet (this may be the first story to produce one, if Phase 3's dual-host test surfaces a genuine finding — see that phase's notes). [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md) is not directly applicable here — this story adds no new async method to any call chain, only DI/SDK registration.

## Architecture Pre-Flight Finding

The `architecture-advisor` agent flagged a real conflict: `standards/backend-architecture.md`'s cross-cutting-concerns table currently reads `OpenTelemetry SDK registration (tracing + metrics) | Program.cs`, but this plan (per the brainstorm's Q1) registers OTel inside `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` instead — matching every other DI/infra registration in this codebase (`DbContext`, `HttpClient`, handler registrations) and issue #3's explicit design goal that `Program.cs`'s shape stay stable. Resolved: the table entry is stale (written before the `AddXInfrastructure()` convention was established) and is corrected in Phase 4 below, in the same change, per [governance/architecture-docs-edit-gate.md](../../governance/architecture-docs-edit-gate.md).

## Implementation Steps

### Phase 1: NuGet packages

- [x] Add `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, and `OpenTelemetry.Instrumentation.Http` to `src/EventLedger.Gateway/EventLedger.Gateway.csproj` via `dotnet add package <name>` (resolves latest stable, net8.0-compatible version), then pin the exact resolved version explicitly in the `.csproj`, matching this repo's existing convention (`Microsoft.EntityFrameworkCore.Sqlite Version="8.0.11"`, `Serilog.AspNetCore Version="8.0.3"` — no floating/wildcard versions anywhere). Resolved to `1.16.0` for all three.
- [x] Add `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Instrumentation.AspNetCore` **only** (no `OpenTelemetry.Instrumentation.Http`) to `src/EventLedger.AccountService/EventLedger.AccountService.csproj`. The Account Service makes zero outbound HTTP calls — per [standards/service-boundaries.md](../../standards/service-boundaries.md), it must "Remain callable and testable with zero outbound HTTP dependencies" and must not "Call the Gateway, or any other service, for any reason" — so instrumenting an `HttpClient` it never uses would be unjustified complexity, not "harmless consistency." Resolved to `1.16.0`.

### Phase 2: OTel SDK registration and inbound-extraction test (OBS-1)

- [ ] Write test: `tests/EventLedger.Gateway.Tests/GatewayTraceparentPropagationTests.cs` — send a request to `/health` with an inbound `traceparent` header set to a known, well-formed W3C value (`00-<32 known hex chars>-<16 hex chars>-01`), using the same `Console.SetOut`/`StringWriter`/`try`-`finally` redirection technique as `GatewayLoggingTests.cs`, then assert the captured JSON log's `TraceId` field equals the exact 32-hex-char trace ID from the header (proves OBS-1's "extracts," not just "generates" — the existing `GatewayLoggingTests.Request_ProducesJsonLogLineWithServiceNameAndTraceId` test already covers the "generates" case for a request with no inbound header and must keep passing unchanged).
- [ ] Confirm this test is red (OpenTelemetry SDK not yet registered — ASP.NET Core's bare hosting `Activity` may or may not honor the inbound header; either way, nothing wires up the W3C-aware extraction this story requires).
- [ ] Implement: extend `AddGatewayInfrastructure(this WebApplicationBuilder builder, string serviceName)` in `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` — add a `serviceName` parameter, and inside the method body add:
  ```csharp
  builder.Services.AddOpenTelemetry()
      .ConfigureResource(r => r.AddService(serviceName))
      .WithTracing(tracing => tracing
          .AddAspNetCoreInstrumentation()
          .AddHttpClientInstrumentation());
  ```
  No span exporter is registered (per the brainstorm's Q2) — the only requirement is that `Activity.Current` is correctly populated (inbound-`traceparent`-aware, outbound-injecting) so the existing `TraceLoggingMiddleware`/Serilog enrichment carries a real, propagated trace ID.
- [ ] Implement: extend `AddAccountServiceInfrastructure(this WebApplicationBuilder builder, string serviceName)` in `src/EventLedger.AccountService/Infrastructure/ServiceCollectionExtensions.cs` the same way, but `.WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())` only — no `AddHttpClientInstrumentation()`, per Phase 1's package decision.
- [ ] Implement: update both `Program.cs` call sites to pass the service name — `builder.AddGatewayInfrastructure("EventGateway");` / `builder.AddAccountServiceInfrastructure("AccountService");` — reusing the exact same literal string already passed to `BootstrapLogging(...)` two lines above in the same file (per the brainstorm's Q4: one source of truth for the value, not an independently-typed second occurrence in a different subsystem). This changes one existing call's argument, not `Program.cs`'s line count or shape.
- [ ] Confirm the new inbound-extraction test now passes.
- [ ] Confirm `GatewayLoggingTests.cs` and `AccountServiceLoggingTests.cs` (from issue #3) still pass unchanged — regression check for OBS-1's "generates" case now that OTel, not bare ASP.NET Core diagnostics, populates `Activity.Current`.

### Phase 3: Cross-service propagation test (OBS-2)

- [ ] Write test: add a new test method to `tests/EventLedger.Gateway.Tests/GatewayToAccountServiceFullFlowTests.cs` (reusing its existing `CreateFactories()` helper — no new dual-host wiring needed). Redirect `Console.Out` (same `try`/`finally` technique as `GatewayLoggingTests.cs`) around a real `POST /events` call through both in-process hosts, then parse **all** `TraceId`-bearing JSON lines from the captured shared stream and assert at least two distinct lines carry the identical `TraceId` value.
  - **Deliberately do not assert `ServiceName`-per-line correctness in this test.** `BootstrapLogging(serviceName)` reassigns Serilog's process-wide static `Log.Logger`; when both services boot in one xUnit process via two `WebApplicationFactory` instances, whichever service's `BootstrapLogging` call runs last (the Account Service, since it boots lazily mid-request) can make later Gateway-side log lines carry the wrong `ServiceName`. `LogContext.PushProperty("TraceId", ...)` is `AsyncLocal`-scoped and unaffected by this, so `TraceId` values stay correct regardless — asserting on `TraceId` alone sidesteps a test-environment-only artifact that cannot occur in production (each service runs in its own OS process there). Leave a code comment explaining this, matching this codebase's existing pattern of leaving contextual reasoning comments where the "why" isn't obvious from the code alone (e.g. `GatewayLoggingTests.cs`'s own comment about why filtering on `"ServiceName"` alone can't narrow a log line).
- [ ] Run the test. **This is a genuine verification step, not a formality** — Phase 2's registration already exists by this point, so this test isn't validating not-yet-written code; it's checking whether trace-context propagation actually survives the specific in-process `TestServer.CreateHandler()` transport this dual-host test topology uses, which is a different code path than a real network hop. If it passes immediately, proceed. If it's unexpectedly red, that is real information requiring investigation (not a simple fix-the-typo situation) — determine why, fix the actual gap, and if it reveals a non-obvious `TestServer`/in-process propagation nuance, write it up as a new `docs/patterns/YYYY-MM-DD-<slug>.md` entry per this project's established practice (see the four existing pattern docs, all born from exactly this kind of empirically-discovered gap during TDD).

### Phase 4: Documentation

- [ ] Update `standards/backend-architecture.md`'s "Where cross-cutting concerns live" table: change the `OpenTelemetry SDK registration (tracing + metrics)` row's location from `Program.cs` to reflect the `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` convention actually used (per the Architecture Pre-Flight Finding above). Note in the row or a nearby line that metrics registration (the `(+ metrics)` part of that row) is issue #5's scope, not this story's — this plan only touches tracing.

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../standards/backend-architecture.md#test-project-layout). No SQLite/persistence surface touched by this story, so the "real file-based SQLite, never `InMemory`" rule doesn't apply here — but the dual-host test in Phase 3 does reuse `GatewayToAccountServiceFullFlowTests.cs`'s existing real-SQLite wiring incidentally, since it's the same test class. `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is already present in both test projects' `AssemblyInfo.cs` (added in issue #3 for exactly this kind of `Console.Out`/static-`Log.Logger` shared-state hazard) — no new configuration needed.

### Test Cases

- **Description**: A Gateway request carrying an inbound `traceparent` header produces a log line whose `TraceId` matches that header's trace ID, not a freshly-generated one.
  **Type**: Integration (real `WebApplicationFactory<Program>`, `Console.Out` capture).
  **Edge cases**: N/A — one well-formed header value is sufficient to prove extraction works; malformed-`traceparent`-header handling is OpenTelemetry SDK behavior, not something this story's code decides.
  **Phase reference**: Phase 2.
- **Description** (regression, no new test needed): A Gateway request with no inbound `traceparent` header still produces a valid-format (32 lowercase hex chars) `TraceId` in the log.
  **Type**: Integration — already covered by `GatewayLoggingTests.Request_ProducesJsonLogLineWithServiceNameAndTraceId` (issue #3); must remain green after Phase 2.
  **Phase reference**: Phase 2 (verification only, no new test file).
- **Description**: A single `POST /events` driven through the real Gateway→Account-Service wiring produces at least two log lines (one per service) sharing the same `TraceId`.
  **Type**: Integration (dual `WebApplicationFactory`, `TestServer.CreateHandler()`, `Console.Out` capture).
  **Edge cases**: None planned beyond the happy path — this story's acceptance criteria (OBS-1, OBS-2) don't require testing failure-path trace propagation (e.g. Account Service unreachable), and inventing that scenario here would be scope creep beyond what's asked.
  **Phase reference**: Phase 3.

## Decisions Made

1. **OTel registration location**: folded into `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()`, not `Program.cs` — matches every other infra/DI registration in this codebase; `standards/backend-architecture.md`'s table (which said `Program.cs`) is corrected in Phase 4 to match, since the table predates this convention (brainstorm Q1, confirmed via architecture pre-flight).
2. **No span exporter registered** — the story's only requirement is `Activity.Current` population for Serilog's existing `TraceId` enrichment; `architecture/observability.md` explicitly treats a real backend (Jaeger/Zipkin/OTLP) as unnecessary for this local, solo-run system (brainstorm Q2).
3. **OBS-2 verified by an automated test**, not manual-only — reuses `GatewayToAccountServiceFullFlowTests.cs`'s existing dual-host wiring and `GatewayLoggingTests.cs`'s existing `Console.Out`-capture technique rather than inventing a third pattern (brainstorm Q3).
4. **OTel resource `service.name` reuses the same literal already passed to `BootstrapLogging(serviceName)`**, threaded via a new `serviceName` parameter on `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` — one source of truth between Serilog's `ServiceName` log property and OTel's resource attribute (brainstorm Q4).
5. **The dual-host test asserts `TraceId` equality only, not per-line `ServiceName` correctness** — `BootstrapLogging`'s static `Log.Logger` reassignment collides across two in-process-booted services in a way that's a recognized test-environment-only artifact (not a production bug, since real deployments are separate OS processes); `TraceId` itself, being `AsyncLocal`-scoped via `LogContext`, is unaffected (planning-time risk resolution).
6. **Account Service registers `AddAspNetCoreInstrumentation()` only, not `AddHttpClientInstrumentation()`** — it makes zero outbound HTTP calls per `standards/service-boundaries.md`, so instrumenting an `HttpClient` it never uses would be unjustified complexity (planning-time YAGNI correction to the brainstorm's looser "include both for consistency" framing).

### Known Constraints

- The custom OpenTelemetry `Counter` metric and the `/health` DB-connectivity upgrade mentioned in the "Phase 2 - Observability" milestone description belong to issue #5 ("Story 4: Observability — structured logging, health checks, custom metric"), confirmed via `gh issue list --milestone`. This plan is tracing only.
- Exact OpenTelemetry package versions are resolved and pinned at implementation time via `dotnet add package` (no OpenTelemetry packages exist in the local NuGet cache to pre-determine a specific version during planning) — follow this repo's existing exact-version-pinning convention once resolved.
