---
issue: 7
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/7
branch: 7_graceful-degradation
base: master
plan: docs/plans/7_graceful-degradation-plan.md
---

# Handoff: Story 6 — Graceful Degradation

## Release Notes

This story closes out the Resiliency phase by proving the system degrades
the way it's supposed to when the Account Service goes down, and by
adding the one piece of surface area that was still missing: a way for
clients to check a balance through the Gateway at all.

Two of the four acceptance items (RES-6: `POST /events` returns `503`
with nothing persisted during an outage, and RES-7: `GET /events/{id}`
and `GET /events?account=...` keep working regardless) turned out to
already be true — they fell straight out of the confirm-before-persist
design from Story 1 and the resilience pipeline from Story 5. This story
adds tests that prove it rather than just assume it, including a test
that checks a follow-up `GET` returns `404` after a failed `POST`, not
just that the `POST` itself returned the right status code.

The genuinely new work is a `GET /accounts/{accountId}/balance` endpoint
on the Gateway (RES-8) — until now, checking a balance meant calling the
Account Service directly, which isn't supposed to be reachable from
outside. The new endpoint is a thin passthrough: it calls the Account
Service through the same hardened, retry/circuit-breaker-wrapped HTTP
client `POST /events` already uses, relays the response body back
unchanged on success, and returns a `503` with the same error shape
`POST /events` already uses when the Account Service can't be reached.

The last item is the "Verified" acceptance criterion: if the Gateway
crashes after the Account Service has already confirmed a transaction but
before the Gateway's own database write lands, can a client safely retry?
The design's answer has always been "yes, because the Account Service
itself refuses to double-apply the same `eventId`" — no outbox, no
two-phase commit. This story adds a test that proves it against the real
Account Service, not a stub: it plants a transaction directly against the
Account Service (simulating "already confirmed, Gateway hasn't heard back
yet"), then has the Gateway process the identical request as a client
retry would, and confirms the balance only moved once.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| New `GET /accounts/{accountId}/balance` endpoint (`AccountsController`, `BalanceQueryHandler`) | Small — one new route, one new handler, no changes to any existing endpoint or shared registration beyond one `AddScoped` line | Whether the passthrough is genuinely verbatim (no re-serialization drift) and whether the `503` error mapping matches `POST /events`'s existing shape | Unit tests for all three `BalanceQueryHandler` branches (success, exception, non-success status) plus integration tests through the real controller for both the `200` and `503` paths; reuses the same named `HttpClient` + Polly pipeline already exercised by issue #6's resiliency tests, so no new untested resilience code path was introduced |
| RES-6/RES-7 verification (no production code changed) | None — these tests assert behavior of code that already shipped in issues #2 and #6 | Whether the new tests actually prove the guarantee (persistence/non-persistence) rather than just re-checking a status code | The RES-6 test asserts a follow-up `GET` returns `404`, not just that the `POST` returned `503`; the RES-7 test asserts both read endpoints return the correct seeded data while the Account Service handler is failing |
| Crash-then-retry idempotency proof (no production code changed) | None — asserts an existing guarantee (the Account Service's `eventId` unique constraint, from issue #2) | Whether the test's simulated "pre-crash" step is a faithful reconstruction of the real failure mode, not a weaker stand-in | The test calls the real Account Service directly (bypassing the Gateway) to recreate the exact post-crash database state, then drives the retry through the real Gateway against the real Account Service — no stubs in either direction, and the final assertion checks the Account Service's own balance, not just an HTTP status code |
| `architecture/gateway-architecture.md` endpoint table update | Trivial — one table row added | N/A | Docs-only change, required by this repo's architecture-docs-edit-gate rule |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| Balance handler returns the Account Service's raw response body verbatim on success | written | `BalanceQueryHandlerTests.GetBalanceAsync_AccountServiceReturnsSuccess_ReturnsBodyVerbatim` |
| Balance handler maps an unreachable Account Service (exception) to `AccountServiceUnavailable` | written | `BalanceQueryHandlerTests.GetBalanceAsync_AccountServiceUnreachable_ReturnsUnavailable` |
| Balance handler maps a non-success status response to `AccountServiceUnavailable` | written | `BalanceQueryHandlerTests.GetBalanceAsync_AccountServiceReturnsNonSuccessStatus_ReturnsUnavailable` |
| `GET /accounts/{accountId}/balance` returns `200` with the Account Service's exact body | written | `AccountsControllerTests.GetBalance_AccountServiceReachable_ReturnsBalanceBodyVerbatim` |
| `GET /accounts/{accountId}/balance` returns `503` with the standard unavailable envelope | written | `AccountsControllerTests.GetBalance_AccountServiceUnreachable_Returns503WithStandardEnvelope` |
| `POST /events` during an outage returns `503` and persists nothing (RES-6) | written | `EventsControllerTests.PostEvents_AccountServiceUnreachable_Returns503AndPersistsNothing` |
| `GET /events/{id}` and `GET /events?account=...` unaffected by an outage (RES-7) | written | `EventsControllerTests.GetEventById_And_ListByAccount_UnaffectedByAccountServiceOutage` |
| Client retry after a Gateway crash (post-confirmation, pre-commit) applies exactly once ("Verified" item) | written | `GatewayToAccountServiceFullFlowTests.PostEvents_AccountServiceAlreadyConfirmedBeforeGatewayCrash_RetrySucceedsWithoutDoubleApplying` |
| (unplanned) Shared `StubResponseHandler` test double | added | Extracted during the `/simplify` pass after `AccountsControllerTests.cs` and `BalanceQueryHandlerTests.cs` each independently introduced an identical fixed-status-and-body stub handler in the same diff |

### What's Not Tested

The `[ExcludeFromCodeCoverage]`-eligible DI wiring line in
`ServiceCollectionExtensions.cs` (the new `AddScoped<BalanceQueryHandler>()`
registration) has no dedicated unit test — it's exercised indirectly by
every `AccountsControllerTests` case, since those tests boot the real DI
container. No production behavior in this diff is untested; RES-6, RES-7,
and the crash-retry guarantee were already covered by design (see Release
Notes) rather than being new code, and this story's tests exist
specifically to make that verifiable rather than assumed.
