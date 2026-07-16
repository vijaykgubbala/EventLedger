# Resiliency

The Gateway's call to the Account Service is the system's one network hop
between services, and the only place a resiliency pattern is required. This
document defines what's applied there and why, and the graceful-degradation
behavior when the Account Service is unavailable.

## Pattern: circuit breaker + timeout (primary), retry (secondary)

Implemented as a **Polly v8 resilience pipeline** wrapping the `HttpClient`
the Gateway uses to call the Account Service, composed of:

1. **Timeout** — bounds how long the Gateway will wait for a single Account
   Service call before treating it as failed.
2. **Circuit breaker** — after a threshold of failures (timeouts or error
   responses) within a window, opens the circuit and fails subsequent calls
   immediately (without attempting the network call) for a cooldown period,
   then allows a trial request through (half-open) to test recovery.
3. **Retry** — a short retry (few attempts, small fixed/backoff delay)
   layered *underneath* the circuit breaker, for transient blips only (e.g.
   a single dropped connection) — not a substitute for the circuit breaker,
   and not allowed to retry indefinitely.

Pipeline order (outermost to innermost): circuit breaker → timeout → retry.
The circuit breaker sits outermost so that once it's open, calls fail
immediately without even attempting a timeout/retry cycle — that's the
point of "stop calling it temporarily."

### Configured values

| Strategy | Setting | Value |
|---|---|---|
| Timeout | Per-attempt timeout | 2 seconds |
| Retry | Max attempts | 2 retries (3 attempts total) |
| Retry | Delay | 200ms, fixed (not exponential) |
| Retry / Circuit breaker | What counts as a failure | A 5xx status code, or any exception (network failure, or a timed-out attempt). A `4xx` never counts — it's deterministic (the same request produces the same `4xx` again), so retrying it can't help and it shouldn't count against the circuit's failure ratio either. |
| Circuit breaker | Failure ratio to open | ≥50% |
| Circuit breaker | Sampling window | 10 seconds |
| Circuit breaker | Minimum throughput | 4 calls sampled in the window |
| Circuit breaker | Break duration | 5 seconds, then half-open |

A timed-out attempt is itself retriable under the shared failure predicate
above — a genuinely hung Account Service doesn't necessarily recover on
retry, so a hung call's worst-case latency is closer to `attempts ×
timeout + (attempts − 1) × retry delay` (≈6.4s with these values) than a
single 2-second timeout. Still bounded, per RES-2 — just not as tight as
the per-attempt timeout alone would suggest.

Implemented in `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs`,
chained onto the `AddHttpClient("AccountService", ...)` registration via
`Microsoft.Extensions.Http.Resilience`'s `AddResilienceHandler("account-service", ...)`.

### Why circuit breaker + timeout as the *primary* pattern

The assignment lists three acceptable patterns: circuit breaker, bulkhead,
timeout+retry. Circuit breaker + timeout is chosen as primary because it
directly addresses the two ways the Account Service can hurt the Gateway:

- **Timeout bounds latency.** Without one, a hung Account Service call
  hangs the Gateway's handling of that request indefinitely — the
  assignment explicitly calls out "rather than hanging" as the failure mode
  to avoid for `POST /events`. A timeout is what turns "hanging" into "a
  bounded wait, then a clear error."
- **Circuit breaker fails fast under sustained failure.** Once the Account
  Service is confirmed to be down (not just one flaky call), continuing to
  attempt calls — even with a bounded timeout each — means every request
  still pays that timeout cost before failing. The breaker skips straight
  to failure once it's confident the dependency is down, which is what
  keeps the Gateway responsive under sustained Account Service outage
  rather than merely bounded-slow.
- **A bare retry (without a breaker) risks a de-facto hang under sustained
  failure.** If the Account Service is fully down and the Gateway retries
  every single request some number of times with backoff, the *effective*
  per-request latency during an outage is `timeout × attempts` for every
  request — which is functionally the same hanging behavior a timeout was
  supposed to prevent, just multiplied. Retry is only safe as a
  *secondary* layer, bounded to a couple of attempts, meant to smooth over
  a single transient blip rather than to ride out a real outage.

Bulkhead was not chosen as primary: this is a single-operator local system
with a small number of endpoints and no concurrent-load requirement in the
assignment. Bulkheading protects a thread/connection pool from being
exhausted by one failing dependency under *concurrent* load — a real
concern at production traffic volumes, not a meaningful risk for this
system's scale. The circuit breaker's fail-fast behavior already prevents
calls from piling up against a known-down dependency, which covers the
practical risk bulkhead would otherwise guard against here.

## Graceful degradation

When the Account Service is unavailable (call fails, times out, or the
circuit is open):

| Gateway endpoint | Behavior |
|---|---|
| `POST /events` | Return `503 Service Unavailable` with a message indicating the Account Service is unreachable. Nothing is persisted (see [vertical-architecture.md](vertical-architecture.md#core-decision-confirm-before-persist-no-outbox)). Never hang, never return `500`. |
| `GET /events/{id}` | Still works — served entirely from the Gateway's own local data, no dependency on the Account Service. |
| `GET /events?account=...` | Still works, same reason. |
| Balance queries (proxied to the Account Service) | Return a clear error (`503`) indicating the Account Service is unreachable — not a `500`, not a hang, not a stale/cached value pretending to be current. |

This table is the concrete expression of the assignment's requirement that
the system "behave gracefully when parts of the system are unavailable" —
every row is either "still works because it doesn't need the dependency" or
"fails fast and clearly because it does."

## Anti-patterns to avoid

- **Do not retry without a circuit breaker (or retry indefinitely).** A
  bare retry-with-backoff loop against a genuinely down dependency produces
  exactly the unbounded-latency behavior a timeout is meant to prevent —
  see above.
- **Do not let a Gateway request hang waiting on the Account Service with
  no timeout.** Every outbound call to the Account Service goes through the
  Polly pipeline; there is no direct, unwrapped `HttpClient` call to it
  anywhere in the Gateway.
- **Do not return `500` for an Account Service outage.** `503` is the
  correct status — the Gateway itself is healthy; a downstream dependency
  is not.
- **Do not make `GET /events/{id}` or `GET /events?account=...` call, wait
  on, or otherwise depend on the Account Service's availability.** They
  must degrade to "fully working" during an Account Service outage, not
  "degraded."
- **Do not add a bulkhead, rate limiter, or additional resiliency library
  beyond the Polly pipeline described here "for completeness."** One
  well-justified primary pattern (plus the secondary retry layer) satisfies
  the requirement; stacking on more patterns without a driving need is
  exactly the kind of complexity this system is scaled to avoid.
