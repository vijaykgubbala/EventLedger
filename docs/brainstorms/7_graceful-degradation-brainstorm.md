# Brainstorm: Graceful Degradation (503 behavior, local-data-only reads stay up)

**Date:** 2026-07-16
**Issue:** #7

## Problem Statement

When the Account Service is unavailable, `POST /events` must fail clearly
(`503`, nothing persisted — never a hang, never a `500`), while
`GET /events/{id}` and `GET /events?account=...` must keep working since
they never depend on the Account Service. A new
`GET /accounts/{accountId}/balance` passthrough endpoint must be added to
the Gateway (it doesn't exist yet) that proxies to the Account Service and
returns a clear `503` when unreachable. Separately, the design must be
shown safe against the crash scenario where the Gateway dies after the
Account Service confirms a transaction but before the Gateway's own local
insert commits — a client retry after that crash must not double-apply,
relying on the Account Service's own `eventId` idempotency rather than an
outbox.

## Codebase Context

- **`architecture/resiliency.md`** (Graceful degradation table, lines
  89–104) already documents the target behavior for all four
  cases — `POST /events` → 503/nothing persisted; both GET reads → "still
  works, served entirely from the Gateway's own local data"; balance
  queries → 503, not a stale/cached value pretending to be current.
- **`src/EventLedger.Gateway/Controllers/EventsController.cs`** already
  maps `SubmitEventOutcome.AccountServiceUnavailable` →
  `StatusCode(503, new { error = "account_service_unavailable", message = ... })`
  (lines 39–41). `GetById`/`ListByAccount` call only `EventQueryHandler`
  — no `HttpClient` anywhere in this file.
- **`src/EventLedger.Gateway/Application/EventQueryHandler.cs`** is pure
  `GatewayDbContext` reads (`AsNoTracking`), zero outbound calls. RES-7 is
  true by construction today.
- **`src/EventLedger.Gateway/Application/SubmitEventHandler.cs`** already
  catches `HttpRequestException or ExecutionRejectedException` (the
  common base for Polly's `TimeoutRejectedException`/
  `BrokenCircuitException`, from issue #6) and maps to
  `AccountServiceUnavailable` before any local `db.Events.Add`/
  `SaveChangesAsync` — persistence never happens on an unreachable
  Account Service. RES-6 is true by construction today.
- **`src/EventLedger.AccountService/Controllers/AccountsController.cs`**
  already exposes `GET /accounts/{accountId}/balance` →
  `Ok(new { accountId, balance })`, always `200` (unknown accounts return
  balance `0`, never `404`, per `standards/api.md`). This is what the new
  Gateway endpoint proxies to.
- **No passthrough/proxy pattern exists anywhere in the Gateway yet** —
  grepped for `HttpClient`/`IHttpClientFactory` usage; the only call site
  is `SubmitEventHandler`'s transaction-apply flow. The balance endpoint
  is genuinely new surface, not an extension of an existing pattern.
- **`src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs`**
  already registers `AddHttpClient("AccountService", ...)` wrapped in the
  full resilience pipeline (circuit breaker → retry → timeout, correctly
  per-attempt after issue #6's F1 fix). A new handler can reuse this same
  named client with no new DI registration beyond adding the handler
  itself to the service collection (line 56–59 pattern).
- **`standards/api.md`**: the `503` "Account Service unreachable" status
  row (line 20) already explicitly covers "Gateway only: `POST /events`,
  balance-related reads" — this story's `503` behavior was anticipated,
  not invented. The shared error envelope (`{error, message, details?}`,
  lines 37–58) is the same shape `SubmitEventHandler` already uses.
- **`docs/patterns/2026-07-15-cancellation-token-propagation.md`**
  applies directly — a new balance handler must thread `CancellationToken`
  from controller → handler → `HttpClient.GetAsync(..., cancellationToken)`.
- **`architecture/vertical-architecture.md`** (lines 57–100, "confirm
  before persist, no outbox"): explicitly states the Account Service is
  independently idempotent on `eventId`, so a Gateway retry — or a client
  retry after a Gateway crash/timeout — is always safe. This is the
  mechanism the "Verified" acceptance item needs to demonstrate with a
  test, not build new infrastructure for.
- **`architecture/gateway-architecture.md`** (lines 20–21, 24–31): the
  "Responsibilities" prose already says the Gateway should "proxy balance
  reads to the Account Service and surface a clear error if it's
  unreachable," but the endpoint table itself has no balance row yet —
  needs updating in this change per the architecture-docs-edit-gate rule.
- **Reusable test doubles** in `tests/EventLedger.Gateway.Tests/EventsControllerTests.cs`:
  `FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue)` gives
  an "always unreachable" stub directly reusable for the new balance
  endpoint's unreachable-case test, with no new fixed-status stub needed
  (see `docs/simplify-patterns.md`'s "configurable stub subsumes a fixed
  one" entry).

## Q&A Decisions

**Q1: How should we simulate the Gateway-crashes-mid-flow scenario in a test, since we can't literally kill the process?**
A: Inject a fault before `SaveChangesAsync` — force `SubmitEventHandler` to fail right after the Account Service call succeeds but before the local commit, then replay the same `eventId` and assert the retry succeeds without double-applying.

**Q2: Where should the new balance passthrough endpoint live?**
A: New `AccountsController` (`src/EventLedger.Gateway/Controllers/AccountsController.cs`) plus a new `Application/BalanceQueryHandler.cs`, mirroring the `HealthCheckHandler` extraction precedent from issue #5. Matches the Account Service's own `AccountsController` naming.

**Q3: What should the success/error response shapes be?**
A: Bare passthrough of the Account Service's `{ accountId, balance }` body on success; reuse the exact `{ error: "account_service_unavailable", message: ... }` shape `SubmitEventHandler` already returns for `POST /events` on failure, per `standards/api.md`'s shared error envelope.

**Q4: Does RES-6/RES-7 need new production code?**
A: No — verification tests only. Both are true by construction from issues #2 and #6; this story adds tests proving it, not new implementation.

**Q5: Where do the new tests live?**
A: New `AccountsControllerTests.cs` for the balance endpoint; RES-6/RES-7/crash-scenario tests added as new cases inside the existing `EventsControllerTests.cs`, since they exercise `POST`/`GET /events` which that file already covers.

## Proposed Approaches

### Approach 1: Minimal passthrough, verification-only for RES-6/7 (Recommended)

New `AccountsController` + `BalanceQueryHandler` reusing the existing
`"AccountService"` named `HttpClient` and its resilience pipeline
unchanged. Balance responses are a bare passthrough of the Account
Service's own body; failures reuse the existing `account_service_unavailable`
error shape. RES-6/RES-7 get new tests only, no new production code. The
crash scenario gets a dedicated fault-injection test in
`SubmitEventHandler`'s existing test file, proving idempotent retry via
the Account Service's own `eventId` uniqueness — no outbox, no new
persistence mechanism.

**Pros:**
- Matches every documented architecture decision with zero new
  infrastructure — reuses the resilience pipeline, the error envelope,
  and the confirm-before-persist model exactly as designed.
- Smallest possible diff for a ~0.25h-estimated story; most of the
  "work" is proving existing behavior, not building new behavior.
- No new anti-patterns introduced (no caching, no new DI surface beyond
  one controller/handler pair).

**Cons:**
- Relies on trusting that RES-6/RES-7 are truly already correct rather
  than re-deriving them from scratch — mitigated by the explicit
  verification tests this approach still adds.

### Approach 2: Balance endpoint with a local last-known-value cache

Same controller/handler shape as Approach 1, but on an unreachable
Account Service, serve the last successfully-fetched balance from a local
cache/table instead of a `503`, with a staleness flag in the response.

**Pros:**
- Superficially more "available" — a caller always gets *some* number.

**Cons:**
- Directly contradicts `architecture/resiliency.md`'s explicit
  requirement: "not a stale/cached value pretending to be current." This
  isn't a style preference, it's a documented anti-pattern for this
  exact endpoint.
- Adds a new persistence concern (cache invalidation, staleness
  tracking) with no corresponding acceptance criterion asking for it —
  pure scope creep against a ~0.25h budget.

### Approach 3: Generic request-forwarding proxy mechanism

Instead of a purpose-built balance handler, build a general "forward this
request to the Account Service and relay its response" middleware/handler
that any future passthrough endpoint could reuse.

**Pros:**
- Would make a hypothetical second passthrough endpoint cheaper to add
  later.

**Cons:**
- Only one passthrough endpoint is required by the assignment — this is
  speculative generality for a requirement that doesn't exist yet,
  directly against this project's stated YAGNI stance
  (`architecture/vertical-architecture.md`'s system-shape section).
- A generic proxy mechanism obscures the specific error-mapping and
  status-code logic (`503` + the exact error envelope) that this
  endpoint needs, making the one endpoint that does exist harder to
  reason about for no present benefit.

## Recommendation

**Approach 1.** It's the only option that doesn't either violate a
documented architecture decision (Approach 2) or add unrequested
generality (Approach 3). All five Q&A decisions point the same direction:
minimal new surface, maximal reuse of what issues #2, #5, and #6 already
built and proved correct.

## Related Docs

- [architecture/resiliency.md](../../architecture/resiliency.md)
- [architecture/gateway-architecture.md](../../architecture/gateway-architecture.md)
- [architecture/vertical-architecture.md](../../architecture/vertical-architecture.md)
- [docs/plans/6_resiliency-plan.md](../plans/6_resiliency-plan.md)
- [docs/handoffs/2026-07-16-085854-6_resiliency-handoff.md](../handoffs/2026-07-16-085854-6_resiliency-handoff.md)
- [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md)
- [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md)
- [docs/simplify-patterns.md](../simplify-patterns.md)
