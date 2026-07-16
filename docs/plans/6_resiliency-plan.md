# Resiliency — Polly circuit breaker + timeout + retry pipeline

**Issue:** #6

## Context

This plan implements issue #6's five resiliency acceptance criteria (RES-1 through RES-5) by wrapping the Gateway's single outbound call to the Account Service (`POST /accounts/{accountId}/transactions`, made from `SubmitEventHandler`) in a Polly v8 pipeline via `Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler`. See [docs/brainstorms/6_resiliency-brainstorm.md](../brainstorms/6_resiliency-brainstorm.md) for the full research, Q&A, and rejected alternative (a hand-rolled `ResiliencePipeline` + custom `DelegatingHandler`, which reinvents what the library's fluent API already provides).

`architecture/resiliency.md` already specifies the pipeline's shape and order (circuit breaker outermost → timeout → retry innermost) — this plan is implementation of that settled decision, plus supplying the numeric thresholds the doc was previously silent on (recorded back into the doc in Phase 4, per this project's rule that architecture decisions land in the owning doc, not just a plan).

## Relevant Learnings

No `docs/solutions/` entries are directly about Polly/resiliency yet. [docs/solutions/observability/metrics-middleware-exception-safety-2026-07-16.md](../solutions/observability/metrics-middleware-exception-safety-2026-07-16.md) is loosely related in spirit (instrumentation/infrastructure code silently swallowing a failure case it exists to handle) but not directly reusable — that story was about a metrics middleware dropping a counter increment, not an HTTP resilience pipeline. [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md) already confirmed `SubmitEventHandler.cs`'s outbound call correctly threads `cancellationToken` — no action needed there, verified during research (Explore agent confirmed the token reaches `PostAsJsonAsync` at the current line).

## Architecture Pre-Flight Finding

No conflict. The `architecture-advisor` agent confirmed the pipeline shape/order matches `architecture/resiliency.md`'s existing "circuit breaker → timeout → retry" spec exactly, the exception-to-outcome mapping matches the doc's graceful-degradation table (`503`, nothing persisted, never a `500`), and scoping the pipeline to the Gateway only (never the Account Service, which has zero outbound calls) is correct per `standards/service-boundaries.md`. Recording the numeric thresholds into `resiliency.md` in the same change satisfies `architecture/README.md`'s editing rule.

## Implementation Steps

### Phase 1: NuGet package

- [x] Add `Microsoft.Extensions.Http.Resilience` to `src/EventLedger.Gateway/EventLedger.Gateway.csproj` via `dotnet add package Microsoft.Extensions.Http.Resilience` (resolves latest stable, net8.0-compatible version — could not be determined during planning, no local NuGet cache entry and no internet access at plan time; resolve and pin explicitly at execution time, matching this repo's existing convention of exact `Version="X.Y.Z"` pins, no wildcards). `Polly` itself (including `Polly.ExecutionRejectedException`, `Polly.Timeout.TimeoutRejectedException`, `Polly.CircuitBreaker.BrokenCircuitException`) comes in transitively — confirm the transitive version exposes these types at execution time; no separate direct `PackageReference` to `Polly` is needed unless the transitive version doesn't. Resolved to `10.8.0`, pulling in `Polly.Core 8.4.2`/`Polly.Extensions 8.4.2`/`Polly.RateLimiting 8.4.2` transitively (Polly v8, as expected). Build succeeds.

### Phase 2: Resilience pipeline registration (RES-1)

- [x] Implement: `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` — chain `.AddResilienceHandler("account-service", builder => { ... })` onto the existing `AddHttpClient("AccountService", ...)` call (lines 28-29). Pipeline name `"account-service"` (matches the `AddHttpClient` name, lowercased — no existing project convention for Polly pipeline keys, confirmed via research, so this establishes one). Add `using Microsoft.Extensions.Http.Resilience;` and `using Polly;`/`using Polly.CircuitBreaker;`/`using Polly.Timeout;`/`using Polly.Retry;` as needed to the top-of-file usings. **Note**: `AddResilienceHandler` resolved without needing an explicit `using Microsoft.Extensions.Http.Resilience;` (extension method resolution worked via the existing `IServiceCollection`/`IHttpClientBuilder` usings already in scope); `using Polly.Timeout;` wasn't needed either since `AddTimeout(TimeSpan)` and `ExecutionRejectedException` didn't require it directly. Compiled clean on first attempt.
  - Inside the builder, in `.Add()` order (outer-to-inner = circuit breaker, timeout, retry):
    ```csharp
    builder
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 4,
            BreakDuration = TimeSpan.FromSeconds(5),
            ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome))
        })
        .AddTimeout(TimeSpan.FromSeconds(2))
        .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Constant,
            ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome))
        });
    ```
  - `IsTransientFailure(Outcome<HttpResponseMessage> outcome)` — a small local/private static helper shared by both `ShouldHandle` predicates (per Q5: retry/circuit-breaker treat the same set as "failure" — 5xx status code, or any exception including `HttpRequestException` and the inner `TimeoutRejectedException` the retry strategy would see after the timeout strategy trips): `outcome.Exception is not null || (outcome.Result is { } response && (int)response.StatusCode >= 500)`. A `400` (or any other non-5xx status) returns `false` — never retried, never counted as a circuit-breaker failure, per Q5's decision.
- [x] Implement: `src/EventLedger.Gateway/Application/SubmitEventHandler.cs` — widen the existing `catch (HttpRequestException ex)` block (current lines 42-46) to also catch `Polly.ExecutionRejectedException` (the common base for `TimeoutRejectedException`/`BrokenCircuitException`), both mapping to the existing `SubmitEventOutcome.AccountServiceUnavailable` — no new outcome variant. C# syntax: `catch (Exception ex) when (ex is HttpRequestException or Polly.ExecutionRejectedException)`. Add `using Polly;` to the file's usings if not already present via a shared import. Full Gateway test suite (49 tests) still passes with the pipeline now active on every DI-registered `HttpClient("AccountService")` call, confirming no regression.

### Phase 3: New stateful test doubles + integration tests (RES-2 through RES-5)

- [x] Implement: two new private sealed `HttpMessageHandler` subclasses in `tests/EventLedger.Gateway.Tests/EventsControllerTests.cs`, alongside the existing `StubAccountServiceHandler` (current lines 148-152):
  - `HangingAccountServiceHandler` — `SendAsync` awaits `Task.Delay(delay, cancellationToken)` then returns a fixed status (or never returns if the delay exceeds the token's lifetime — the `cancellationToken` passed to `Task.Delay` is what lets the pipeline's timeout strategy actually cut it short; confirm the delay used in the RES-2 test, e.g. 5s, comfortably exceeds the pipeline's 2s timeout).
  - `FlakyAccountServiceHandler(int failuresBeforeSuccess, HttpStatusCode failureStatus = HttpStatusCode.InternalServerError)` — stateful (a private `int` call counter field), returns `failureStatus` for the first `failuresBeforeSuccess` calls, then `HttpStatusCode.Created` afterward. Used for RES-4 (circuit half-opens and closes on a successful trial call) and RES-5 (single transient failure recovers via retry).
- [x] Add a `CreateFactory` overload (or a new private helper) in `EventsControllerTests.cs` that accepts an `HttpMessageHandler` instance directly (rather than just a status code), so the new stateful handlers can be wired in via `ConfigurePrimaryHttpMessageHandler(() => handlerInstance)` — mirrors the existing `CreateFactory(HttpStatusCode)` shape but takes the handler itself, since the stateful handlers need to be constructed and held by the test (to assert call counts) before being wired into the factory.
- [x] Write test: RES-2 — `PostEvents_AccountServiceHangs_TimesOutAndReturns503` — wire a `HangingAccountServiceHandler` (delay comfortably longer than 2s), issue `POST /events`, assert the response arrives within a bounded time (e.g. well under the hang's full delay, proving the timeout — not the hang — determined the response time) with status `503` and the existing `AccountServiceUnavailable`-shaped error body. Handler delay 30s, bound asserted at 10s; actual observed time ~6s, matching the expected worst case of 3 attempts × 2s timeout + 2 × 200ms retry delay ≈ 6.4s (a timed-out attempt is itself retriable under the shared `IsTransientFailure` predicate, so a hang doesn't resolve after just one timeout — confirms genuine pipeline engagement, not a fluke).
- [x] Write test: RES-5 — `PostEvents_AccountServiceFailsOnceThenSucceeds_RetryRecoversTransparently` — wire a `FlakyAccountServiceHandler(failuresBeforeSuccess: 1)`, issue `POST /events`, assert `201 Created` (the caller never sees the transient failure — retry absorbed it) and assert the handler's call count is `2` (one failure + one success, proving retry actually happened, not that the first call happened to succeed).
- [x] Write test: RES-3 — `PostEvents_SustainedFailures_OpensCircuitAndFailsFastWithoutNetworkAttempt` — wire a `FlakyAccountServiceHandler` configured to always fail (or a very high `failuresBeforeSuccess`), issue enough `POST /events` calls to exceed the circuit breaker's minimum throughput (4) at ≥50% failure ratio to trip it open, then issue one more call and assert (a) the response is still `503`, and (b) the handler's call count did **not** increase for that last call (proving the circuit was open and no network attempt was made — the core RES-3 assertion). Confirmed the circuit breaker (outermost strategy) observes one outcome per `POST /events` call, not one per internal retry attempt — 4 always-failing calls reliably trip it (verified across 4 consecutive runs, no flakiness).
- [x] Write test: RES-4 — `PostEvents_CircuitOpensThenCooldownElapses_HalfOpensAndClosesOnSuccess` — trip the circuit open the same way as the RES-3 test, wait past the 5s break duration (real wait, per the brainstorm's Q1 decision), reconfigure or rely on the `FlakyAccountServiceHandler` to succeed on the next call (the half-open trial), issue one more `POST /events`, and assert `201 Created` — proving the circuit closed again after a successful trial call. **Deviation**: `FlakyAccountServiceHandler`'s `failuresBeforeSuccess` was made a mutable property (not just a constructor value), since RES-4 needs to switch the handler from "always fails" (to trip the circuit) to "always succeeds" (for the half-open trial) mid-test — a `handler.FailuresBeforeSuccess = 0` flip right before the trial call, rather than a second handler class. Verified reliable across 2 runs (~11s each, dominated by the 6s cooldown wait).
- [x] Confirm all new tests pass, and existing tests (`SubmitEventHandlerTests.cs`'s 7 tests, `EventsControllerTests.cs`'s existing tests, `GatewayToAccountServiceFullFlowTests.cs`) remain green — the resilience pipeline sits in the same `AddHttpClient("AccountService")` registration those dual-host tests already use, so a regression here would show up broadly. Note: `EventsControllerTests.cs`'s test-host `services.AddHttpClient("AccountService")` call (current lines 25-26) does **not** clear the production `.AddResilienceHandler(...)` chain — ASP.NET Core merges same-named `AddHttpClient` builder actions additively — so the pipeline is active in all existing `EventsControllerTests.cs` tests too; this is expected and desired (RES-1 says the pipeline applies to *the* outbound `HttpClient`, not a special test-only path), but means the existing fixed-status-code tests' timing is now bounded by a real (if generous) 2s timeout ceiling per attempt rather than instant — confirm none of them regress into flakiness from this. Full solution: 82/82 passing (29 Account Service + 53 Gateway); `GatewayToAccountServiceFullFlowTests.cs`'s 3 dual-host tests specifically re-run and confirmed green with the pipeline active.

### Phase 4: Documentation

- [x] Update `architecture/resiliency.md` — add a new subsection (after the existing "Pipeline order" paragraph, before "Why circuit breaker + timeout as the *primary* pattern") recording the configured numeric thresholds: timeout 2s per attempt; retry 2 attempts, 200ms fixed delay, only for 5xx + network/timeout failures (never a `400`); circuit breaker opens at ≥50% failure ratio over a 10s sampling window with minimum throughput 4, breaks for 5s. Keep the existing rationale prose below it unchanged. Added a "Configured values" table plus a note explaining a hung call's worst-case latency is ≈6.4s (not a flat 2s), since a timed-out attempt is itself retriable under the shared failure predicate — matches RES-2's actual observed test timing.

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../standards/backend-architecture.md#test-project-layout). No new test-only NuGet package needed — confirmed via research that the new stateful handlers need only `System.Threading.Tasks` primitives already available via existing `Microsoft.AspNetCore.Mvc.Testing`/`xunit` references. Per the brainstorm's Q1 decision, RES-2 through RES-5's tests use small-but-real durations and genuinely wait — no fake `TimeProvider`/fault-injection scaffolding. `[assembly: CollectionBehavior(DisableTestParallelization = true)]` is already present in this test project; the new tests don't share `Console.Out`/`Log.Logger` state, but running in the same serialized suite avoids any risk of two circuit-breaker tests' timing windows overlapping.

### Test Cases

- **Description**: A hung Account Service call is bounded by the pipeline's timeout, not indefinite — the Gateway returns `503` within a bounded time even though the stub handler's delay is much longer.
  **Type**: Integration (`WebApplicationFactory<Program>` + new `HangingAccountServiceHandler`).
  **Edge cases**: Assert the response arrives well before the handler's full delay would have elapsed, not just that it eventually arrives — otherwise the test can't distinguish "timeout worked" from "the test just waited it out."
  **Phase reference**: Phase 3, RES-2.
- **Description**: A single transient failure (one 500, then success) recovers transparently via retry — the caller sees `201 Created`, not `503`.
  **Type**: Integration (`WebApplicationFactory<Program>` + new `FlakyAccountServiceHandler(failuresBeforeSuccess: 1)`).
  **Edge cases**: Assert the handler's call count is exactly `2`, not just that the final response is `201` — a call count of `1` would mean the first call happened to succeed, proving nothing about retry.
  **Phase reference**: Phase 3, RES-5.
- **Description**: Sustained failures (enough to cross the circuit breaker's minimum throughput at ≥50% failure ratio) open the circuit; a subsequent call fails immediately with no network attempt.
  **Type**: Integration (`WebApplicationFactory<Program>` + `FlakyAccountServiceHandler` configured to always fail).
  **Edge cases**: Assert the handler's call count does not increase for the call made after the circuit is expected to be open — this is the assertion that actually proves "no network attempt," not just that the response was `503` (which could also happen via an ordinary failed call).
  **Phase reference**: Phase 3, RES-3.
- **Description**: After the circuit's break duration elapses, the circuit half-opens; a successful trial call closes it again, and subsequent calls succeed normally.
  **Type**: Integration (`WebApplicationFactory<Program>`, real wait past the 5s break duration per Q1).
  **Edge cases**: The trial call itself must succeed for the circuit to close — if it fails, the circuit should re-open, not stay half-open; this plan's test only needs to prove the successful-trial-closes-the-circuit path (RES-4's literal acceptance criterion), not exhaustively test every half-open failure permutation.
  **Phase reference**: Phase 3, RES-4.
- **Description** (regression, no new test needed beyond what Phase 3 already covers): existing `SubmitEventHandlerTests.cs` tests (direct construction, bypassing the pipeline entirely) and `EventsControllerTests.cs`'s existing fixed-status-code tests remain green with the pipeline now active in the DI-registered `HttpClient`.
  **Type**: Existing unit + integration tests, re-run as a regression check.
  **Phase reference**: Phase 3, final confirmation step.

## Decisions Made

1. **`Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler` with a custom pipeline**, not `AddStandardResilienceHandler()` — matches the issue's explicit instruction and the library's purpose-built integration point; a hand-rolled `ResiliencePipeline`/`DelegatingHandler` was considered and rejected as reinventing what the library already does (brainstorm).
2. **Timing strategy: small real durations, tests genuinely wait** — not a fake `TimeProvider` — simplest for this story's small number of resiliency tests; a fake-clock approach is the more "proper" answer for a codebase where this pattern recurs often, disproportionate here (brainstorm Q1).
3. **Numeric values**: timeout 2s; retry 2 attempts/200ms fixed delay; circuit breaker 50% failure ratio/10s sampling/4-call minimum throughput/5s break duration (brainstorm Q2) — recorded into `architecture/resiliency.md` in Phase 4 since the doc was previously silent on numbers.
4. **Exception mapping**: widen `SubmitEventHandler`'s catch to `Polly.ExecutionRejectedException` alongside `HttpRequestException`, both mapping to the existing `SubmitEventOutcome.AccountServiceUnavailable` — no new outcome variant, matching `architecture/gateway-architecture.md`'s existing undifferentiated framing of "error response, timeout, or an open circuit" (brainstorm Q3).
5. **Retry/circuit-breaker `ShouldHandle` predicate**: only 5xx status codes and exceptions (`HttpRequestException`, the inner `TimeoutRejectedException`) count as "failure" — a `400` is never retried and never counted toward the circuit breaker's failure ratio, since it's deterministic and retrying it wastes budget without ever helping (brainstorm Q5). Both the retry and circuit-breaker strategies share one `IsTransientFailure` helper to keep this criterion consistent between the two.
6. **Tests live at the `WebApplicationFactory` integration level**, mirroring `EventsControllerTests.cs`, not `SubmitEventHandlerTests.cs`'s direct-construction level — the latter structurally cannot exercise a pipeline registered via `AddHttpClient(...).AddResilienceHandler(...)`, since it bypasses `IHttpClientFactory` entirely (brainstorm Q4). New stateful test doubles (`HangingAccountServiceHandler`, `FlakyAccountServiceHandler`) are required since nothing existing can simulate a hang or a failure-then-recovery sequence.
7. **Pipeline name**: `"account-service"` — no existing project convention for Polly pipeline keys (confirmed via research); this choice matches the `AddHttpClient("AccountService", ...)` name it's chained onto, lowercased, establishing the convention for any future resilience pipeline in this codebase.
8. **Package version**: could not be resolved during planning (no local NuGet cache entry for `Microsoft.Extensions.Http.Resilience`/`Polly`, no internet access at plan time) — resolved and pinned explicitly at execution time via `dotnet add package`, matching this repo's existing exact-version-pinning convention (same approach used for OpenTelemetry packages in issue #4's plan).

### Known Constraints

- The RES-4 test's real 5s wait (past the circuit breaker's break duration) is the single longest-running test this story adds; acceptable given it's one test, not a recurring pattern across a large suite (brainstorm Q1's tradeoff accepted explicitly).
- Bulkhead and rate-limiter patterns are explicitly out of scope — `architecture/resiliency.md`'s anti-patterns forbid adding them "for completeness" beyond what's asked; this plan implements circuit breaker + timeout + retry only.
