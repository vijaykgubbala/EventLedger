# Graceful Degradation (503 behavior, local-data-only reads stay up)

**Issue:** #7

## Context

Builds on the brainstorm at
[docs/brainstorms/7_graceful-degradation-brainstorm.md](../brainstorms/7_graceful-degradation-brainstorm.md).
RES-6 (`POST /events` → `503`, nothing persisted, during an outage) and
RES-7 (`GET /events/{id}` / `GET /events?account=...` unaffected by an
outage) are already true by construction from issues #2 and #6 — this
plan adds verification tests for both, no new production code. The
genuinely new work is a `GET /accounts/{accountId}/balance` passthrough
endpoint on the Gateway (RES-8) and a dedicated test proving the
crash-then-retry idempotency guarantee ("Verified" acceptance item).

## Relevant Learnings

- [docs/solutions/layering/healthcontroller-dbcontext-injection-2026-07-16.md](../solutions/layering/healthcontroller-dbcontext-injection-2026-07-16.md):
  any controller action touching an external dependency (DB or, here, an
  outbound `HttpClient`) must go through an `Application/` handler, never
  call it directly from the controller — this plan's `BalanceQueryHandler`
  follows that precedent exactly (same as `HealthCheckHandler`).
- [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md):
  `CancellationToken` must thread `AccountsController` action →
  `BalanceQueryHandler.GetBalanceAsync` → `HttpClient.GetAsync(..., cancellationToken)`,
  same as `SubmitEventHandler`'s existing outbound call.
- [docs/simplify-patterns.md](../simplify-patterns.md): reuse
  `FlakyAccountServiceHandler` (already in `EventsControllerTests.cs`) for
  any new "Account Service unreachable" test case rather than adding
  another fixed-status stub — `failuresBeforeSuccess: int.MaxValue` gives
  an always-fails stub, `failuresBeforeSuccess: 0` gives an always-succeeds
  one.
- No prior `docs/solutions/` entry addresses cross-service idempotency
  testing directly, but
  [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md)
  documents the underlying unique-constraint mechanism this plan's crash
  test proves end-to-end.
- Architecture pre-flight (via `architecture-guide`): no conflicts. The
  proposed `AccountsController` + `BalanceQueryHandler` design, the bare
  passthrough response, and the reused `account_service_unavailable`
  error envelope all match `architecture/gateway-architecture.md`,
  `architecture/resiliency.md`, and `standards/api.md` as already
  written. One pre-existing doc gap noted (not introduced by this
  change): `architecture/gateway-architecture.md`'s endpoint table has no
  balance row even though its Responsibilities prose already describes
  the behavior — this plan closes that gap (Phase 5).

## Implementation Steps

### Phase 1: Gateway — Application layer (`BalanceQueryHandler`)

- [x] Write test: `BalanceQueryHandlerTests.cs` (new file, mirroring
  `SubmitEventHandlerTests.cs`'s `StubHttpClientFactory`/unit-test shape)
  — Account Service returns `200` with `{"accountId":"acct-1","balance":150}`
  → handler returns `Outcome = Success` and the raw response body
  unchanged (byte-for-byte, so the controller can relay it verbatim).
- [x] Write test: `BalanceQueryHandlerTests.cs` — Account Service
  unreachable (stub throws `HttpRequestException`, matching
  `SubmitEventHandlerTests.cs`'s existing unreachable-case pattern) →
  handler returns `Outcome = AccountServiceUnavailable`.
- [x] Write test: `BalanceQueryHandlerTests.cs` — Account Service returns
  a non-success status (`500`, simulating retries exhausted without an
  exception, same as `SubmitEventHandler`'s existing
  `!response.IsSuccessStatusCode` branch) → handler returns
  `Outcome = AccountServiceUnavailable`.
- [x] Implement `src/EventLedger.Gateway/Application/BalanceQueryHandler.cs`:
  sealed class, primary-constructor DI (`IHttpClientFactory`), single
  method `GetBalanceAsync(string accountId, CancellationToken cancellationToken = default)`
  returning a `BalanceQueryResult` record (`Outcome` enum: `Success`,
  `AccountServiceUnavailable`; `Body` — the raw JSON string on success,
  `null` otherwise). Calls
  `httpClientFactory.CreateClient("AccountService").GetAsync($"/accounts/{accountId}/balance", cancellationToken)`
  — same named client and resilience pipeline `SubmitEventHandler`
  already uses, no new registration. Catches
  `HttpRequestException or ExecutionRejectedException` exactly like
  `SubmitEventHandler.cs` lines 43–51.

### Phase 2: Gateway — Controllers (`AccountsController`)

- [x] Write test: `AccountsControllerTests.cs` (new file, same
  `WebApplicationFactory<Program>` + stub-handler shape as
  `EventsControllerTests.cs`) — `GET /accounts/{accountId}/balance`
  against a stub Account Service returning `200 {"accountId":"acct-1","balance":150}`
  (reuse `FlakyAccountServiceHandler(failuresBeforeSuccess: 0)` from
  `EventsControllerTests.cs`, or a local equivalent if cross-file reuse
  isn't practical — confirm during execution which is simpler) → Gateway
  returns `200` with the identical body.
  - Cross-file reuse wasn't practical: `FlakyAccountServiceHandler` is
    `private` to `EventsControllerTests.cs`. Used a local
    `StubBalanceHandler(HttpStatusCode, string body)` instead.
- [x] Write test: `AccountsControllerTests.cs` — same call against
  `FlakyAccountServiceHandler(failuresBeforeSuccess: int.MaxValue)` (always
  unreachable) → Gateway returns `503` with
  `{ error: "account_service_unavailable", message: "The Account Service is currently unavailable." }`
  — identical error code and message text to `EventsController`'s
  existing `POST /events` unavailable case.
- [x] Implement `src/EventLedger.Gateway/Controllers/AccountsController.cs`:
  `[ApiController]`, `[Route("accounts")]`, primary-constructor DI
  (`BalanceQueryHandler`), single action
  `[HttpGet("{accountId}/balance")] GetBalance(string accountId, CancellationToken cancellationToken)`.
  Maps `Success` → `Content(result.Body!, "application/json")` (verbatim
  passthrough, no re-serialization/DTO); `AccountServiceUnavailable` →
  `StatusCode(503, new { error = "account_service_unavailable", message = "The Account Service is currently unavailable." })`.
- [x] Register `BalanceQueryHandler` in
  `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs`
  (`builder.Services.AddScoped<BalanceQueryHandler>();`, alongside the
  existing handler registrations at lines 56–59). No other DI or
  `AddHttpClient` changes — reuses the existing `"AccountService"`
  registration untouched.

### Phase 3: RES-6 / RES-7 verification (extend `EventsControllerTests.cs`)

- [ ] Write test: `PostEvents_AccountServiceUnreachable_Returns503AndPersistsNothing`
  — using `CreateFactory(new FlakyAccountServiceHandler(int.MaxValue))`,
  `POST /events` returns `503`, then a follow-up `GET /events/{eventId}`
  for the same `eventId` returns `404` (proving nothing was persisted,
  not just that the response code was right).
- [ ] Write test: `GetEventById_And_ListByAccount_UnaffectedByAccountServiceOutage`
  — seed a record via a factory with a working Account Service stub
  first, then reopen the factory with
  `FlakyAccountServiceHandler(int.MaxValue)` and confirm both
  `GET /events/{id}` and `GET /events?account=...` still return `200`
  with the expected data, proving these reads never touch the Account
  Service even while it's down.

### Phase 4: Crash-then-retry idempotency verification

- [ ] Write test:
  `PostEvents_AccountServiceAlreadyConfirmedBeforeGatewayCrash_RetrySucceedsWithoutDoubleApplying`
  in `GatewayToAccountServiceFullFlowTests.cs`, reusing the existing
  `CreateFactories()` helper (in-process `WebApplicationFactory` pair,
  real Account Service, no stubs — line 50). Recreates the exact
  post-crash state directly rather than via fault injection: first,
  `accountServiceFactory.CreateClient()` POSTs straight to the Account
  Service's own `/accounts/{accountId}/transactions` with a specific
  `eventId` (this is the state right after "the Account Service
  confirmed" and right before "the Gateway crashed" — the Gateway's own
  DB has no record of this `eventId`). Second,
  `gatewayFactory.CreateClient()` POSTs the identical `eventId`/payload to
  `/events` (this is "the client retries after getting no response").
  Assert the Gateway call returns `201 Created` (it finds no local
  record, calls the Account Service again, the Account Service's own
  `eventId` unique constraint returns its existing confirmation instead
  of applying twice, and the Gateway then persists its local record
  normally). Then assert via the Account Service's own
  `GET /accounts/{accountId}/balance` that the transaction amount was
  applied exactly once — the definitive proof, not just that the retry
  didn't error.
  > **Note on Q1's decision:** the brainstorm's approved approach was
  > "inject a fault before `SaveChangesAsync`." This test achieves the
  > identical proof (replay the same `eventId`, assert no double-apply)
  > without building fault-injection machinery — calling the Account
  > Service directly for the "pre-crash" step and then hitting the
  > Gateway for the "retry" step recreates the exact same on-disk state a
  > `SaveChangesAsync` failure would have left, more simply. It also
  > lives in `GatewayToAccountServiceFullFlowTests.cs` rather than
  > `EventsControllerTests.cs`, since it needs the real dual-service
  > wiring that file already has and `EventsControllerTests.cs`'s
  > stub-handler tests don't — a refinement of Q5's file-placement
  > answer, not a reversal of it.

### Phase 5: Docs

- [ ] Update `architecture/gateway-architecture.md`: add a
  `GET /accounts/{accountId}/balance` row to the endpoint table (lines
  24–31), matching the behavior already described in the Responsibilities
  section (lines 20–21) — closes the pre-existing doc gap noted above.

## Testing Strategy

### Test Environment

xUnit, per
[standards/backend-architecture.md](../../standards/backend-architecture.md#test-project-layout).
Phase 1 uses the existing `StubHttpClientFactory` unit-test shape from
`SubmitEventHandlerTests.cs`. Phases 2–3 use
`WebApplicationFactory<Program>` with stub `HttpMessageHandler`s, per
`EventsControllerTests.cs`'s existing pattern. Phase 4 uses real
file-based SQLite for both services via the existing `CreateFactories()`
dual-`WebApplicationFactory` helper — no `InMemory`, no stubs, per
[docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md).

### Test Cases

- **Description**: Balance handler returns the Account Service's raw
  response body verbatim on success.
  **Type**: Unit. **Edge cases**: none beyond the happy path.
  **Phase reference**: Phase 1.
- **Description**: Balance handler maps an unreachable Account Service
  (exception) to `AccountServiceUnavailable`.
  **Type**: Unit. **Edge cases**: `HttpRequestException` and
  `ExecutionRejectedException` both handled. **Phase reference**: Phase 1.
- **Description**: Balance handler maps a non-success status response
  (retries exhausted, no exception thrown) to `AccountServiceUnavailable`.
  **Type**: Unit. **Phase reference**: Phase 1.
- **Description**: `GET /accounts/{accountId}/balance` returns `200` with
  the Account Service's exact body.
  **Type**: Integration. **Phase reference**: Phase 2.
- **Description**: `GET /accounts/{accountId}/balance` returns `503`
  with the standard unavailable envelope when the Account Service is
  unreachable.
  **Type**: Integration. **Phase reference**: Phase 2.
- **Description**: `POST /events` during an outage returns `503` and a
  follow-up `GET /events/{id}` confirms nothing was persisted.
  **Type**: Integration. **Phase reference**: Phase 3 (RES-6).
- **Description**: `GET /events/{id}` and `GET /events?account=...`
  return `200` with correct data while the Account Service is down.
  **Type**: Integration. **Phase reference**: Phase 3 (RES-7).
- **Description**: A client retry with an `eventId` the Account Service
  already confirmed (simulating a Gateway crash before local commit)
  succeeds and applies the transaction exactly once.
  **Type**: Integration (dual real services). **Edge cases**: this is the
  test that would fail if the Account Service's own idempotency guard
  were ever removed or weakened — it's the one place in the suite that
  proves the "no outbox needed" design decision, not just documents it.
  **Phase reference**: Phase 4 ("Verified" acceptance item).

## Decisions Made

- **`BalanceQueryHandler` returns the raw response body string, not a
  deserialized DTO.** Keeps the passthrough genuinely verbatim (byte-for-
  byte) as decided in the brainstorm's Q3, and avoids maintaining a
  duplicate `{accountId, balance}` shape on the Gateway side that could
  drift from the Account Service's own.
- **Crash-scenario test mechanism refined from "fault injection" to
  "direct pre-call + retry"** — see the note under Phase 4. Same
  approved approach (replay `eventId`, assert no double-apply), simpler
  mechanism, more faithful to the literal crash state described in the
  acceptance criterion.
- **Crash-scenario test placed in `GatewayToAccountServiceFullFlowTests.cs`**,
  not `EventsControllerTests.cs` — refines Q5's answer based on the
  technical requirement (real dual-service wiring) discovered while
  designing the test, not a change of intent.

### Known Constraints

- The Account Service's balance endpoint never returns `404` — unknown
  accounts return `balance: 0` with `200` (per `standards/api.md`). The
  Gateway passthrough inherits this; there is no "account not found" case
  to test.
