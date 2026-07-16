---
issue: 4
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/4
branch: 4_distributed-tracing
base: master
plan: docs/plans/4_distributed-tracing-plan.md
---

# Handoff: Distributed tracing — OpenTelemetry, traceparent propagation (review-fix round)

## Release Notes

This is a follow-up handoff for issue #4, covering the `workflow-review 4` fix-application round on top of the original implementation (the first handoff, `docs/handoffs/2026-07-16-000217-4_distributed-tracing-handoff.md`, still describes the original OpenTelemetry SDK registration work — OBS-1 and OBS-2 — unchanged).

Five review findings were fixed:

- **F1/F2 (critical)**: the cross-service integration test that's supposed to prove `traceparent` propagates end-to-end (`PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs`) never asserted the underlying request actually succeeded, and neither it nor the inbound-extraction test would have failed if this story's actual deliverable — the `AddOpenTelemetry()` registration itself — were deleted. Both gaps are closed: the cross-service test now asserts `201 Created` and requires log lines from both services, and a new `OpenTelemetryRegistrationTests.cs` (in both test projects) directly asserts a `TracerProvider` is resolvable via DI, confirmed genuinely red before the registration existed and green after.
- **F3 (warning)**: `ConsoleLogCapture.cs`'s JSON parsing leaked `JsonDocument` buffers; fixed with `.Clone()` on the returned `RootElement`.
- **F4 (warning)**: the real-networking test helper could leak a live Kestrel socket if setup failed partway through; fixed with a dispose-on-failure guard.
- **F5 (warning)**: a fourth, untouched copy of the `Console.Out`-capture pattern existed in the Account Service test project; mirrored `ConsoleLogCapture.cs` into that project (a deliberate per-project copy, same rationale as `SqliteTempDbFixture`) and corrected `docs/simplify-patterns.md`'s wording.
- **F6 (suggestion)**: skipped — asserting the OTel resource's `service.name` attribute would require standing up an in-memory span exporter solely for one test, which is disproportionate for this project's scope and inconsistent with `architecture/observability.md`'s deliberate no-exporter decision.

Applying F1 immediately surfaced that the "real Kestrel networking" test helper built during the original implementation had never actually worked — the test had been passing for the wrong reason since it was first written. `WebApplicationFactory<TEntryPoint>` turns out to unconditionally force `TestServer` as its `IServer` unless that registration is removed from inside an overridden `CreateHost` (not `WithWebHostBuilder`, which runs too early), a dynamic OS-assigned port never resolves to a real bound address in this hosting configuration, and once `IServer` genuinely is Kestrel, the factory's own `.Server`/`.Services`/`.CreateClient()` accessors throw `InvalidCastException`. All three findings are documented in a new pattern doc, [docs/patterns/2026-07-16-webapplicationfactory-forces-testserver.md](../patterns/2026-07-16-webapplicationfactory-forces-testserver.md), alongside a correction to the existing [docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md](../patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md) (its original "Right" example documented the broken dynamic-port approach as if it worked).

Separately: issue #2's PR #13 has merged to `master` since this branch was opened, so `master...HEAD` now produces the identical diff `2_core-functionality...HEAD` did — this PR is retargeted to `master` as part of this handoff, resolving the branch-sequencing deviation noted in the original handoff.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| Test infrastructure only (`GatewayToAccountServiceFullFlowTests.cs`, `ConsoleLogCapture.cs` x2, `OpenTelemetryRegistrationTests.cs` x2) | Small — no production `src/` code changed in this round | Whether `CreateFactoriesWithRealNetworking()`'s fixed port (`58734`) could collide with something else on a reviewer's machine; whether the `InvalidCastException` catch in that helper is too broad | Fixed port only used inside one test class already gated by `[assembly: CollectionBehavior(DisableTestParallelization = true)]`; the catch is scoped to `InvalidCastException` specifically, with a separate broader catch that disposes and rethrows for genuine startup failures |
| Documentation (`docs/plans/4_distributed-tracing-plan.md`, two pattern docs, `docs/reviews/4_distributed-tracing.json`) | Small — docs only | Whether the plan's correction accurately reflects what shipped, given the original plan recorded a since-disproven "confirmed passing, run 5x with no flakiness" claim | Corrections are inline in the plan (not silently rewritten) and cross-reference the new pattern doc and the commit SHA that fixed it |

## Test Coverage

### Planned vs Actual

This round wasn't itself planned (it's `workflow-review` fix-application, not a new plan cycle) — reconciling against `docs/plans/4_distributed-tracing-plan.md`'s original Testing Strategy: the plan's Phase 3 test case is unchanged in intent (single `POST /events` over a genuine network call, one shared `TraceId`) but its implementation approach changed twice, both times documented inline in the plan (see Decisions Made, item 7 and its follow-up correction).

| Test | Status | Notes |
|---|---|---|
| `PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs` | changed | Now asserts `201 Created` + `>=2` log lines (F1); underlying helper rewritten to genuinely use real Kestrel (see Release Notes) |
| (unplanned) `GatewayHost_RegistersTracerProvider` | added | New — closes F2's regression-coverage gap for OTel SDK registration |
| (unplanned) `AccountServiceHost_RegistersTracerProvider` | added | New — same, Account Service side |

### What's Not Tested

No new production code shipped in this round, so there's no new application-behavior gap. The one deliberately-skipped finding (F6, OTel resource `service.name` attribute) remains untested — a single, directly-inspectable line (`ConfigureResource(r => r.AddService(serviceName))`) judged not worth a dedicated in-memory exporter for this project's scope; see the disposition recorded in `docs/reviews/4_distributed-tracing.json`.
