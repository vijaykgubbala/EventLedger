---
issue: 4
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/4
branch: 4_distributed-tracing
base: 2_core-functionality
plan: docs/plans/4_distributed-tracing-plan.md
---

# Handoff: Story 3: Distributed tracing — OpenTelemetry, traceparent propagation

## Release Notes

This PR wires up OpenTelemetry tracing across both services so that a single client request produces one connected trace: the Gateway generates or continues a W3C `traceparent`, propagates it automatically to the Account Service on the one outbound HTTP call, and both services' existing structured JSON logs carry the same `TraceId` for that request — with no hand-rolled header code anywhere.

`architecture/observability.md` already specified this design in full; this story is implementation of a settled decision. `AddOpenTelemetry().ConfigureResource(...).WithTracing(...)` is folded into each service's existing `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` methods (the Gateway registers both ASP.NET Core and HttpClient instrumentation; the Account Service registers ASP.NET Core instrumentation only, since it makes zero outbound calls). No span exporter is registered — the only requirement is that `Activity.Current` is populated correctly so the existing `TraceLoggingMiddleware`/Serilog enrichment (built in issue #3) carries a real, propagated trace ID. That middleware needed zero code changes.

The most consequential part of this story wasn't the registration itself, it was two findings surfaced by actually running the tests rather than trusting the plan, each documented and resolved in the open:

1. The planned inbound-extraction test passed *before* any OpenTelemetry code existed anywhere in this codebase — ASP.NET Core's hosting layer already extracts an inbound `traceparent` into `Activity.Current` independently of OpenTelemetry. Per this project's own TDD rule ("if a test passes without implementation, it isn't asserting the right thing"), this triggered further investigation instead of being accepted at face value.
2. That investigation found `System.Net.Http.DiagnosticsHandler` — the component that actually injects the `traceparent` header — lives inside `SocketsHttpHandler` itself, not as a separately composable layer. Any test that substitutes a custom `HttpMessageHandler` for the primary handler — including this codebase's own `TestServer.CreateHandler()`-based dual-host test from issue #2 — bypasses it entirely, regardless of OpenTelemetry registration. Confirmed empirically (a throwaway diagnostic showed two independently-generated trace IDs instead of one propagated one). This invalidated the original test design from the brainstorm; surfaced directly rather than silently worked around, and resolved by switching the cross-service test to a real Kestrel listener on a dynamically-assigned port, so the Gateway's real `SocketsHttpHandler` makes a genuine network call. Documented as a reusable pattern: `docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md`.

A `/simplify` pass after the four implementation phases extracted a shared `ConsoleLogCapture` test helper (the Console.Out-capture/JSON-TraceId-parsing pattern had grown to three near-identical copies), factored out duplicated `DbContext`-override setup between the two dual-host test factory helpers, deduplicated the service-name literal in each `Program.cs` into a single `const`, and renamed a test file whose sole test no longer matched what it actually verified. This is the first `/simplify` pass in this repo to seed `docs/simplify-patterns.md`.

**Branch base note**: this branch is based on `2_core-functionality` (issue #2), not `master`, since issue #2's own PR (#13) is still open and this story's code extends functionality only issue #2 delivers (the named `"AccountService"` HttpClient, `AddGatewayInfrastructure()`, `GatewayToAccountServiceFullFlowTests.cs`). This PR's diff is scoped to just this story's own commits; it will need to be retargeted to `master` (or will do so automatically if `2_core-functionality` is deleted after PR #13 merges).

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| OpenTelemetry SDK registration (both services' `Infrastructure/ServiceCollectionExtensions.cs`) | Small — additive DI registration, no exporter, no change to request/response behavior | Confirm no span exporter was accidentally left registered (this story deliberately configures none) | Regression tests for existing `/health` logging behavior (issue #3) confirmed still passing unchanged |
| Cross-service trace propagation (the actual feature) | Medium — this is the story's core deliverable | `GatewayToAccountServiceFullFlowTests.cs`'s `CreateFactoriesWithRealNetworking()` and the real-networking test | A genuine network call over a real, dynamically-assigned Kestrel port — not a mock — run 3x post-refactor with no flakiness observed |
| Test infrastructure change (`TestServer.CreateHandler()` → real Kestrel for one specific test) | Small, test-only — no production code path changed | Why `CreateFactories()` (TestServer-based) was kept unchanged for the other two tests in the same file, and only the new tracing test uses the real-networking variant | Documented in `docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md`; both factory helpers now share `ConfigureAccountServiceDb()`/`ConfigureGatewayDb()` to keep the DB wiring identical between them |
| `standards/backend-architecture.md` doc correction | Small — documentation only | The corrected table row now says OTel registration lives in `Infrastructure/`, matching every other DI/infra row, not `Program.cs` as it previously (incorrectly) stated | Caught by the `architecture-advisor` pre-flight during planning, not discovered late |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| Gateway request with inbound `traceparent` header → same `TraceId` in the log | written | `InboundTraceparentExtractionTests.cs` (renamed from `GatewayTraceparentPropagationTests.cs` during `/simplify` — see Release Notes) |
| Gateway request with no inbound header → still produces a valid-format `TraceId` (regression) | written | Already covered by issue #3's `GatewayLoggingTests.cs`, confirmed still green after OTel registration |
| Cross-service `TraceId` propagation via the real Gateway→Account-Service wiring | written | (unplanned) Original plan called for reusing `TestServer.CreateHandler()`; execution found this fundamentally can't observe real header injection (see Release Notes) and switched to a real Kestrel + dynamic-port approach instead |
| (unplanned) Outbound header-injection test via a stub `HttpMessageHandler` | abandoned | Written during investigation, confirmed genuinely red, then removed once understood to be structurally incapable of ever going green — real-networking test supersedes it |

### What's Not Tested

Failure-path trace propagation (e.g. what happens to tracing when the Account Service is unreachable) isn't tested — this story's acceptance criteria (OBS-1, OBS-2) only require the happy-path propagation and log-correlation behavior; the Account Service's specific error-handling paths are already covered by issue #2's tests and aren't re-tested here for tracing-specific behavior, since that would be scope creep beyond what's asked. Span/duration data isn't tested since no exporter is registered — this story's requirement is `TraceId` correlation in logs, not full distributed-tracing observability (explicitly out of scope per `architecture/observability.md`'s "sufficient... without a separate trace backend").
