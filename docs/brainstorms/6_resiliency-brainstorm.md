# Brainstorm: Resiliency — Polly circuit breaker + timeout + retry pipeline

**Date:** 2026-07-16
**Issue:** #6

## Problem Statement

Issue #6 requires wrapping the Gateway's one outbound call (`POST /accounts/{accountId}/transactions` on the Account Service, made from `SubmitEventHandler`) in a resilience pipeline so that:

- **RES-1**: circuit breaker + timeout implemented on the Gateway's outbound `HttpClient`.
- **RES-2**: a hung Account Service call is bounded by a total timeout, not indefinite.
- **RES-3**: sustained failures open the circuit; subsequent calls fail immediately, no network attempt.
- **RES-4**: circuit half-opens after cooldown, closes on a successful trial call.
- **RES-5**: a single transient failure recovers via a small, bounded retry (not unbounded).

The issue body already mandates the pipeline composition mechanism: `AddResilienceHandler` with a **custom** pipeline (`AddCircuitBreaker` → `AddTimeout` → `AddRetry`, in that `.Add()` order = outer-to-inner wrapping) — explicitly **not** `AddStandardResilienceHandler()`, whose preset ordering puts the circuit breaker inside the retry loop.

## Codebase Context

`architecture/resiliency.md` specifies the pipeline shape (circuit breaker outermost, so an open circuit fails immediately without attempting timeout/retry; retry innermost, for transient blips only, "not a substitute for the circuit breaker, and not allowed to retry indefinitely") but gives **zero numeric thresholds** — timeout duration, failure ratio, sampling window, minimum throughput, break duration, and retry count/delay are all open. The doc's graceful-degradation table is explicit: `POST /events` → `503` on Account Service failure (error response, timeout, or open circuit), nothing persisted, "never hang, never return `500`." Anti-patterns forbid a bare retry without a breaker, any unwrapped/direct `HttpClient` call, returning `500` for an outage, and adding bulkhead/rate-limiter/extra libraries "for completeness."

`src/EventLedger.Gateway/Application/SubmitEventHandler.cs` currently obtains the client via `httpClientFactory.CreateClient("AccountService")` and has a single `try/catch (HttpRequestException ex)` around `PostAsJsonAsync`, mapping both the catch and any non-success status code to `SubmitEventOutcome.AccountServiceUnavailable`. It does **not** currently catch a timeout/cancellation exception — a hung call not otherwise bounded would propagate unhandled, which is exactly the gap RES-2 needs to close.

`src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` registers the client as:
```csharp
builder.Services.AddHttpClient("AccountService", client =>
    client.BaseAddress = new Uri(builder.Configuration["AccountService:BaseUrl"]!));
```
This is the exact chain point for `.AddResilienceHandler(...)`. `standards/backend-architecture.md`'s file-placement and cross-cutting-concerns tables already assign "Polly resilience pipeline for the Account Service `HttpClient`" to `Infrastructure/` in `EventLedger.Gateway` only — never registered in the Account Service, per `standards/service-boundaries.md` ("Must not: Issue an unwrapped, un-timed-out `HttpClient` call to the Account Service").

`EventLedger.Gateway.csproj` has no Polly/`Microsoft.Extensions.Http.Resilience` package yet; target framework `net8.0` is compatible.

**Testing gap**: neither existing test double can exercise this story's behavior. `EventsControllerTests.cs`'s `StubAccountServiceHandler` returns a fixed status code instantly — no delay, no call-count-dependent sequencing. `SubmitEventHandlerTests.cs` constructs `SubmitEventHandler` directly with a hand-rolled stub factory that bypasses `IHttpClientFactory`'s real pipeline entirely, so it structurally cannot exercise a resilience handler registered via `AddHttpClient(...).AddResilienceHandler(...)` — that behavior only exists once `IHttpClientFactory` builds the real handler chain. New, more capable test doubles (one that hangs via `Task.Delay`, one that fails a configurable number of times before succeeding) are required regardless of which approach is chosen below.

No `docs/patterns/`/`docs/solutions/` entry exists yet on Polly/resiliency specifically. The `docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md` finding (custom primary `HttpMessageHandler` bypasses `DiagnosticsHandler`) does **not** apply here — `AddResilienceHandler` inserts a `DelegatingHandler` above the primary handler in `IHttpClientFactory`'s chain, so swapping the primary handler (as `EventsControllerTests.cs` already does) should still route through a registered resilience pipeline. Worth confirming empirically once implemented, not assuming.

## Q&A Decisions

**Q1: Testing timing strategy — small real durations (tests wait a few real seconds) vs. a fake `TimeProvider` (tests run instantly)?**
A: Small real durations. Simplest to implement, no extra test infrastructure, and only a handful of resiliency tests exist so the real-time cost is small and one-time — not a recurring tax across a large suite. A fake-clock approach is the more "proper" answer for a codebase where this pattern recurs often, but disproportionate complexity for one story here.

**Q2: Specific numeric values?**
A: Timeout 2s per attempt. Retry: 2 retries (3 total attempts), 200ms fixed delay. Circuit breaker: opens at ≥50% failure ratio over a 10s sampling window with a minimum throughput of 4 calls, breaks for 5s before half-opening.

**Q3: How should Polly's thrown exceptions (`TimeoutRejectedException`, `BrokenCircuitException`, retry-exhausted `HttpRequestException`) map to `SubmitEventOutcome`?**
A: Widen the existing catch clause to also catch `Polly.ExecutionRejectedException` (the common base for `TimeoutRejectedException` and `BrokenCircuitException`) alongside `HttpRequestException`, mapping all of them to the existing `AccountServiceUnavailable` outcome — no new outcome variant. Matches `architecture/gateway-architecture.md`'s own framing, which already treats "error response, timeout, or an open circuit" as one undifferentiated case.

**Q4: Where should RES-2–RES-5's tests live, given `SubmitEventHandlerTests.cs`'s direct-construction approach can't exercise a registered pipeline?**
A: New integration tests at the `WebApplicationFactory` level, mirroring `EventsControllerTests.cs`'s `WithWebHostBuilder` + `ConfigurePrimaryHttpMessageHandler` pattern (which goes through the real DI-registered `HttpClient`). New stateful test doubles are needed — a hang-simulating handler and a fail-N-times-then-succeed handler — since nothing existing provides that capability. `SubmitEventHandlerTests.cs`'s existing tests stay unchanged for the logic they already correctly cover.

**Q5: Should retry apply to all non-success responses, or only transient-looking ones (5xx, timeouts, network failures)?**
A: Only 5xx + network/timeout failures. A `400` from the Account Service is deterministic (identical request → identical `400`), so retrying it wastes the retry budget and ~400ms of latency on a call that was never going to succeed, and risks masking a genuine bug in what the Gateway sent. Matches the Gateway's existing validate-before-calling philosophy.

## Proposed Approaches

### Approach 1: `Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler` fluent API

Chain `.AddResilienceHandler("account-service-pipeline", builder => builder.AddCircuitBreaker(...).AddTimeout(...).AddRetry(...))` directly onto the existing `AddHttpClient("AccountService", ...)` call in `ServiceCollectionExtensions.cs`. This is the library's purpose-built integration point for exactly this scenario — it wires the composed `ResiliencePipeline<HttpResponseMessage>` into `IHttpClientFactory`'s handler chain as a `DelegatingHandler` automatically.

**Pros:**
- Directly matches the issue's explicit instruction (`AddResilienceHandler` with a custom pipeline, not the standard preset).
- No manual `DelegatingHandler` plumbing — the package handles registration, DI, and `IHttpClientFactory` integration.
- `ShouldHandle` predicates for timeout/retry/circuit-breaker are all first-class fluent options (status-code and exception-based), directly supporting Q5's "retry only 5xx + network/timeout" decision.

**Cons:**
- None identified relative to the alternative — this is the idiomatic, library-intended path for the exact composition already specified.

### Approach 2: Hand-rolled `ResiliencePipeline` + custom `DelegatingHandler`

Build a `ResiliencePipeline<HttpResponseMessage>` manually via `ResiliencePipelineBuilder<HttpResponseMessage>`, wrap it in a hand-written `DelegatingHandler` that calls `pipeline.ExecuteAsync(...)` around `base.SendAsync(...)`, and register that handler manually via `.AddHttpMessageHandler(...)`.

**Pros:**
- Slightly more control over the handler's internals, if some future requirement needed logic `AddResilienceHandler` doesn't expose.

**Cons:**
- Reinvents exactly what `AddResilienceHandler` already does — more code, more surface for a subtle registration-order bug, no capability gained for this story's actual requirements.
- Directly contradicts `architecture/resiliency.md`'s anti-pattern against adding complexity "for completeness" beyond what's asked.

## Recommendation

**Approach 1.** It's not just simpler — it's what the issue explicitly specifies, it's the maintained, idiomatic integration point `Microsoft.Extensions.Http.Resilience` exists for, and Approach 2 offers no capability this story needs in exchange for meaningfully more code and risk. Combined with the Q&A decisions above: chain `AddResilienceHandler` onto the existing `AddHttpClient("AccountService", ...)` in `ServiceCollectionExtensions.cs` (Infrastructure/, Gateway only), widen `SubmitEventHandler`'s catch clause to `Polly.ExecutionRejectedException` alongside `HttpRequestException`, and test RES-2–RES-5 via new `WebApplicationFactory`-level integration tests with new stateful (hang-simulating, fail-then-recover) test doubles.

## Related Docs

- [architecture/resiliency.md](../../architecture/resiliency.md) — owning design doc; this brainstorm's numeric decisions (Q2) should be folded back into it during planning, per the project's rule that architecture changes land in the owning doc, not just a plan.
- [docs/plans/5_observability-plan.md](5_observability-plan.md) — prior story's plan; issue #6 layers onto the same `SubmitEventHandler`/`ServiceCollectionExtensions.cs` files that issue #5 touched.
