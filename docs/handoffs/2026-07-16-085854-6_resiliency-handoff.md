---
issue: 6
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/6
branch: 6_resiliency
base: master
plan: docs/plans/6_resiliency-plan.md
---

# Handoff: Resiliency — Polly circuit breaker + timeout + retry pipeline

## Release Notes

This PR closes issue #6's five resiliency acceptance criteria (RES-1 through RES-5) by wrapping the Gateway's single outbound call to the Account Service in a Polly v8 resilience pipeline, via `Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler`.

The pipeline composes a circuit breaker (outermost), retry, and timeout (innermost) onto the existing `AddHttpClient("AccountService", ...)` registration: a 2-second **per-attempt** timeout, up to 2 retries with a 200ms fixed delay (only for 5xx status codes or network/timeout exceptions — never a deterministic `4xx`), and a circuit breaker that opens at ≥50% failure ratio over a 10-second sampling window (minimum 4 calls sampled), breaking for 5 seconds before allowing a trial call through. `SubmitEventHandler`'s exception handling was widened to catch `Polly.ExecutionRejectedException` (the common base for a timed-out attempt or an open circuit) alongside the existing `HttpRequestException`, both still mapping to the pre-existing `SubmitEventOutcome.AccountServiceUnavailable` — no new outcome variant, matching the architecture doc's existing framing that a downstream error, a timeout, and an open circuit are all the same story from the caller's perspective (`503`, nothing persisted, never a `500`).

**A `workflow-review` pass caught a genuine, consequential bug in the original implementation**, worth calling out explicitly: the pipeline was originally registered as `.AddCircuitBreaker(...).AddTimeout(...).AddRetry(...)` — this is the exact literal ordering issue #6's own body specifies ("`AddCircuitBreaker` → `AddTimeout` → `AddRetry`, in that `.Add()` order"). In Polly v8's composition semantics, though, this makes the timeout a **single total budget for the entire retry sequence**, not a per-attempt timeout — the opposite of what RES-2's documented worst-case latency and RES-5's "a single transient failure recovers via retry" both require. Three independent review agents converged on this, two of them cross-checking it against the actual local Polly/Microsoft.Extensions.Http.Resilience package documentation, and it was confirmed with direct empirical instrumentation: a hung call was making exactly **one** attempt in ~2.9s, not the three attempts in ~6.4s the architecture doc and the original test comments claimed. The fix reorders to `.AddCircuitBreaker(...).AddRetry(...).AddTimeout(...)` (timeout innermost/per-attempt) — a deliberate deviation from the issue body's literal `.Add()` order, in favor of what the issue's own acceptance criteria and the architecture doc's intent actually require. The RES-2 test was strengthened to assert the handler's call count explicitly (not just an elapsed-time upper bound, which passed identically whether the bug was present or not) — this is what would have caught the defect the first time.

Two smaller review findings were also fixed: `SubmitEventHandler`'s Account-Service-failure logging was corrected from `Error` to `Warning` (matching `standards/logging-dotnet.md`'s classification of these as expected, degraded-mode conditions), and the circuit-breaker recovery test's post-cooldown wait was widened from 6s to 8s for more margin on a slow CI runner.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| Resilience pipeline ordering (`ServiceCollectionExtensions.cs`) | Medium — affects every outbound call to the Account Service, the system's one network hop between services | Whether the corrected `.AddCircuitBreaker(...).AddRetry(...).AddTimeout(...)` order genuinely produces per-attempt timeout behavior, not just a plausible-looking reorder | RES-2's test now asserts `handler.CallCount == 3` explicitly (not just an elapsed-time bound), which is the assertion that actually distinguishes correct behavior from the original bug — confirmed to fail against the pre-fix ordering and pass against the fix |
| `SubmitEventHandler`'s exception mapping | Small — one method, one existing outcome variant reused, no new branch in the Gateway's response mapping | Whether `Polly.ExecutionRejectedException` genuinely covers both a timed-out attempt and an open circuit | Confirmed via RES-2 (timeout path) and RES-3 (circuit-open path) integration tests, both exercising the real DI-registered pipeline, not a mock |
| Retry safety on a financial-transaction flow | Small — reviewed specifically because automatic retries touch a `POST` that applies a transaction | Whether a retried request could double-apply a transaction if an earlier attempt's response was merely lost | Not a new risk: `ApplyTransactionHandler`'s pre-existing unique-constraint-based idempotency guard on `eventId` (from issue #2, untouched by this diff) makes a retried duplicate `eventId` a no-op, confirmed by the `review-security` agent during `workflow-review 6` |
| Circuit-breaker/retry test timing (`EventsControllerTests.cs`) | Small — test-only | Whether the real wall-clock waits (a deliberate, brainstorm-considered tradeoff over a fake `TimeProvider`) introduce CI flakiness | RES-3/RES-4 re-run multiple times during both `workflow-execute` and the `workflow-review` fix cycle with no observed flakiness; the cooldown margin was widened from 6s to 8s as a review finding for extra safety |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| RES-1: circuit breaker + timeout implemented on the Gateway's outbound `HttpClient` | written | Verified indirectly by every other test below actually exercising a registered pipeline via the real DI-registered `HttpClient`, not a mock |
| RES-2: a hung Account Service call is bounded by a timeout, not indefinite | changed | `PostEvents_AccountServiceHangs_TimesOutAndReturns503` — strengthened during `workflow-review` to assert `handler.CallCount == 3`, not just an elapsed-time bound, after that bound was found not to distinguish correct per-attempt-timeout behavior from a total-budget-timeout bug |
| RES-5: a single transient failure recovers via a small, bounded retry | written | `PostEvents_AccountServiceFailsOnceThenSucceeds_RetryRecoversTransparently` — asserts both the final `201 Created` and `handler.CallCount == 2` |
| RES-3: sustained failures open the circuit; a subsequent call fails immediately, no network attempt | written | `PostEvents_SustainedFailures_OpensCircuitAndFailsFastWithoutNetworkAttempt` — asserts the handler's call count is unchanged for the call made after the circuit is expected to be open, which is the assertion that actually proves no network attempt was made |
| RES-4: circuit half-opens after cooldown, closes on a successful trial call | written | `PostEvents_CircuitOpensThenCooldownElapses_HalfOpensAndClosesOnSuccess` — trips the circuit, waits past the break duration (8s margin after `workflow-review`), then asserts a trial call succeeds |
| (unplanned) Existing regression suite remains green with the pipeline active in every `EventsControllerTests.cs` test | added | Not a planned test, but a planned *confirmation* step — the test-host's `AddHttpClient("AccountService")` call doesn't clear the production `.AddResilienceHandler(...)` chain, so the pipeline is live in all 11 `EventsControllerTests.cs` tests, not just the 4 resiliency-specific ones |

### What's Not Tested

Bulkhead and rate-limiter patterns are explicitly out of scope, per `architecture/resiliency.md`'s anti-patterns section — this story implements circuit breaker + timeout + retry only, as the issue specifies. The half-open state's *failure* path (a trial call that fails, re-opening the circuit) is not tested — RES-4's literal acceptance criterion is only the successful-trial-closes-the-circuit path, and testing the failure-reopens path would be scope beyond what's asked. `SubmitEventHandlerTests.cs`'s existing 7 unit tests (direct construction, bypassing `IHttpClientFactory`) remain unchanged and correctly continue to test `SubmitEventHandler`'s branching logic in isolation — they cannot and do not exercise the resilience pipeline itself, which is why all pipeline-behavior tests live in the `EventsControllerTests.cs` integration suite instead.
