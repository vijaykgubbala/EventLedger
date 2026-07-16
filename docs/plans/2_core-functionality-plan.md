# Core Functionality — Idempotency, Out-of-Order Tolerance, Balance Computation, Validation

**Issue:** #2

## Context

Implements the Gateway's `POST /events` and read endpoints, and the
Account Service's transaction-apply and read endpoints, per
[docs/brainstorms/2_core-functionality-brainstorm.md](../brainstorms/2_core-functionality-brainstorm.md)
(v2 — regenerated at the user's request after issue #3 landed real code;
see that document's header for why this supersedes the first pass).

**Dependency:** this plan assumes issue #3 (Service separation, PR
[#11](https://github.com/vijaykgubbala/EventLedger/pull/11)) has merged
to `master` before `workflow-execute 2` runs. Unlike the first version of
this plan, the scaffolding it depends on is no longer hypothetical — its
exact shape is known and referenced directly below (`Infrastructure/ServiceCollectionExtensions.cs`'s
current content, `AppMarker.cs`, the `Program.cs` `try/catch/finally`
wrapper) rather than described generically.

**Scope addition beyond issue #2's original checklist** (carried over
unchanged from the first plan): `GET /accounts/{accountId}/balance`,
`GET /accounts/{accountId}`, `GET /events/{id}`, and
`GET /events?account=...` are included — issue #2's GitHub acceptance
criteria already reflect this (FB-6/7/10/11), updated when the first plan
was written.

**What changed in this regeneration:** two new decisions from the v2
brainstorm (`docs/brainstorms/2_core-functionality-brainstorm.md`'s Q1/Q2)
about exactly *where* new code integrates with issue #3's real delivered
`Infrastructure/ServiceCollectionExtensions.cs` — see Decisions Made
items 7–9. The domain logic, endpoints, and test cases below are
otherwise identical to the first plan; re-verified against `architecture/`
via a second `architecture-guide` pre-flight (clean — the file-organization
questions this regeneration answers are `standards/backend-architecture.md`
territory, outside what `architecture/` governs, and don't conflict with
anything there either).

## Relevant Learnings

- [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md) — the exact attempt-insert/catch-`DbUpdateException`/refetch pattern used in Phase 3 and Phase 4 below.
- [docs/patterns/2026-07-15-cancellation-token-propagation.md](../patterns/2026-07-15-cancellation-token-propagation.md) — every async method below (`Application/` handlers, EF Core calls, the outbound `HttpClient` call) takes and threads a `CancellationToken` through to the next call, not just the first `await`.
- No `docs/solutions/` yet — expected on the first implementation story; nothing to draw from.

## Implementation Steps

### Phase 1: Domain types (both services)

No dedicated tests for this phase — plain types with no framework
dependency, exercised indirectly by every later phase's tests.

- [x] `src/EventLedger.Gateway/Domain/TransactionType.cs` — `enum TransactionType { Credit, Debit }`
- [x] `src/EventLedger.Gateway/Domain/EventRecord.cs` — plain type matching the `events` schema in [architecture/data-model.md](../../architecture/data-model.md) (`Id`, `EventId`, `AccountId`, `Type`, `Amount`, `Currency`, `EventTimestamp`, `MetadataJson`, `ReceivedAt`)
- [x] `src/EventLedger.AccountService/Domain/TransactionType.cs` — same enum, independent copy (no shared assembly between services, per [standards/service-boundaries.md](../../standards/service-boundaries.md))
- [x] `src/EventLedger.AccountService/Domain/TransactionRecord.cs` — matching the `transactions` schema (`Id`, `EventId`, `AccountId`, `Type`, `Amount`, `AppliedAt`)

### Phase 2: Infrastructure — persistence (both services)

- [x] Test: inserting two `EventRecord`s with the same `EventId` throws `DbUpdateException` (integration, Phase 2)
- [x] Test: inserting two `TransactionRecord`s with the same `EventId` throws `DbUpdateException` (integration, Phase 2)
- [x] Test: inserting a row with `Amount <= 0` throws `DbUpdateException` (`CHECK` constraint) for both entity types (integration, Phase 2)
- [x] Implement: `src/EventLedger.Gateway/Infrastructure/GatewayDbContext.cs` — `DbSet<EventRecord>`; `OnModelCreating`: `HasIndex(e => e.EventId).IsUnique()`, `HasCheckConstraint("CK_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0")`, `HasConversion<string>()` on `Type` mapping `TransactionType.Credit`/`.Debit` to the literal strings `"CREDIT"`/`"DEBIT"` (a bare `ToString()` would produce `"Credit"`, breaking both the `CHECK` constraint and the wire contract), composite index on `(AccountId, EventTimestamp)`. **Deviation from original plan text**: the raw check SQL needed an explicit `CAST(... AS REAL)`, not a bare `"Amount" > 0` — see [docs/patterns/2026-07-15-sqlite-decimal-check-constraint-affinity.md](../patterns/2026-07-15-sqlite-decimal-check-constraint-affinity.md) for why (confirmed red via a real TDD run: the bare form silently let `Amount = 0` through).
- [x] Implement: `src/EventLedger.AccountService/Infrastructure/AccountDbContext.cs` — same `UNIQUE` index, `CHECK` constraint (with the same `CAST(... AS REAL)` form), and string conversion; index on `AccountId`
- [x] Implement: extend `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs`'s existing `AddGatewayInfrastructure(WebApplicationBuilder builder)` method **in place** — add `builder.Services.AddDbContext<GatewayDbContext>(opt => opt.UseSqlite(builder.Configuration.GetConnectionString("Gateway")))` as a new line inside the existing method body, after the current `builder.Services.AddControllers();` line. Do not create a new, separately-chained extension method (Decisions Made, item 7).
- [x] Implement: same for `src/EventLedger.AccountService/Infrastructure/ServiceCollectionExtensions.cs`'s `AddAccountServiceInfrastructure(...)`
- [x] Implement: `src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs` — add `EnsureGatewayDatabaseCreated(this WebApplication app)`, an extension **on `WebApplication`** (not `WebApplicationBuilder` — `EnsureCreated()` needs the built app to resolve a scoped `DbContext`, so it structurally cannot live inside `AddGatewayInfrastructure`). Resolves `GatewayDbContext` from a new `IServiceScope` and calls `.Database.EnsureCreated()`.
- [x] Implement: same for the Account Service — `EnsureAccountServiceDatabaseCreated(this WebApplication app)`
- [x] Implement: `src/EventLedger.Gateway/Program.cs` — add exactly one line, `app.EnsureGatewayDatabaseCreated();`, between the existing `var app = builder.Build();` and `app.UseTraceLogging();`. This is the one necessary exception to "`Program.cs`'s shape never changes again" (issue #3's plan) — `EnsureCreated()` operates on the built `app`, which no existing extension method has access to; routing it through a named `Infrastructure/` extension method (not inlining the EF Core call) keeps the *spirit* of the orchestrator rule even though it's technically a new line (Decisions Made, item 9).
- [x] Implement: same one-line addition to `src/EventLedger.AccountService/Program.cs`

### Phase 3: Account Service — Application layer

- [ ] Test: `ApplyTransactionHandler` — new `eventId` → inserts a row, returns the new record (unit, Phase 3)
- [ ] Test: `ApplyTransactionHandler` — duplicate `eventId` → returns the existing record, no second row (ID-3) (integration, Phase 3)
- [ ] Test: `ApplyTransactionHandler` — two concurrent calls with the same `eventId` (`Task.WhenAll`) → exactly one row exists afterward, both calls return the same record (ID-2, Account-Service side) (integration, Phase 3)
- [ ] Test: `ApplyTransactionHandler` — an insert that violates the backstop `CHECK` constraint (bypassing normal validation, e.g. constructing the entity directly in the test) → `500`-mapped result, logged at `Error` (Decisions Made, item 5) (integration, Phase 3)
- [ ] Test: `BalanceQueryHandler` — mixed `CREDIT`/`DEBIT` transactions → `SUM(CREDIT) − SUM(DEBIT)` (FB-10) (unit, Phase 3)
- [ ] Test: `BalanceQueryHandler` — zero transactions for an account → balance `0`, not an error (FB-10 edge case) (unit, Phase 3)
- [ ] Test: `AccountDetailsHandler` — returns `accountId` + full transaction list (FB-11) (unit, Phase 3)
- [ ] Implement: `src/EventLedger.AccountService/Application/ApplyTransactionHandler.cs` — attempt insert; catch `DbUpdateException`; distinguish a `UNIQUE` violation (refetch by `EventId`, return existing, `200`) from a `CHECK` violation (log `Error`, return a result the controller maps to `500`) using the SQLite error code, not just "any `DbUpdateException`"
- [ ] Implement: `src/EventLedger.AccountService/Application/BalanceQueryHandler.cs` — `SUM(CASE WHEN Type = Credit ...) - SUM(CASE WHEN Type = Debit ...)` aggregate query per [architecture/account-architecture.md](../../architecture/account-architecture.md#balance-computation)
- [ ] Implement: `src/EventLedger.AccountService/Application/AccountDetailsHandler.cs` — transaction list + balance for one `accountId`
- [ ] Implement: extend `AddAccountServiceInfrastructure(...)` with explicit `builder.Services.AddScoped<ApplyTransactionHandler>();`, `AddScoped<BalanceQueryHandler>();`, `AddScoped<AccountDetailsHandler>();` (Decisions Made, item 8)

### Phase 4: Gateway — Application layer

- [ ] Test: `EventValidator` — each required field missing individually → failure naming that field (FB-3) (unit, Phase 4)
- [ ] Test: `EventValidator` — `amount <= 0` → failure (FB-4) (unit, Phase 4)
- [ ] Test: `EventValidator` — `type` not exactly `"CREDIT"`/`"DEBIT"`, including wrong case (`"credit"`) → failure (FB-5) (unit, Phase 4)
- [ ] Test: `EventValidator` — a fully valid payload → no failures (unit, Phase 4)
- [ ] Test: `SubmitEventHandler` — valid new event → calls the Account Service, inserts locally, returns the new record (FB-1/FB-2) (integration, Phase 4)
- [ ] Test: `SubmitEventHandler` — `eventId` already stored locally → returns the existing record, Account Service **not** called (ID-1, fast-path) (integration, Phase 4)
- [ ] Test: `SubmitEventHandler` — two concurrent calls with the same new `eventId` → exactly one row exists, the `UNIQUE`-violation path returns the winner's record to the loser (ID-2, Gateway side) (integration, Phase 4)
- [ ] Test: `SubmitEventHandler` — `eventId` resubmitted with a different `amount`/`type` → returns the original unchanged, one `Warning` log line emitted (ID-4) (integration, Phase 4)
- [ ] Test: `SubmitEventHandler` — events for one account submitted with `eventTimestamp`s out of arrival order → balance correct after all are applied (FB-8) (integration, Phase 4)
- [ ] Implement: `src/EventLedger.Gateway/Application/EventValidator.cs`
- [ ] Implement: `src/EventLedger.Gateway/Application/SubmitEventHandler.cs` — fast-path `SELECT` by `EventId`; if not found, call the Account Service via a plain `HttpClient` (**no** Polly pipeline in this story — see Known Constraints); on confirmed success, attempt `INSERT`, handling the `UNIQUE`-violation refetch per [architecture/gateway-architecture.md](../../architecture/gateway-architecture.md#post-events-flow); on the fast-path hit, compare the incoming payload's `Type`/`Amount`/`Currency`/`EventTimestamp` (not `Metadata` — opaque per [standards/events.md](../../standards/events.md)) against the stored record and log one `Warning` line if they differ
- [ ] Implement: extend `AddGatewayInfrastructure(...)` with explicit `builder.Services.AddScoped<EventValidator>();`, `AddScoped<SubmitEventHandler>();` (Decisions Made, item 8)

### Phase 5: Gateway — Controllers

- [ ] Test: `POST /events` with a valid payload → `201` with the full record (FB-1) (integration, Phase 5)
- [ ] Test: `POST /events` with an invalid payload → `400` with `{error, message, details}` per [standards/api.md](../../standards/api.md) (FB-3/FB-4/FB-5) (integration, Phase 5)
- [ ] Test: `GET /events/{id}` for an existing id → `200` with the record (FB-6) (integration, Phase 5)
- [ ] Test: `GET /events/{id}` for a non-existent id → `404` (FB-6) (integration, Phase 5)
- [ ] Test: `GET /events?account={accountId}` → array ordered ascending by `eventTimestamp`, not insertion order (FB-7) (integration, Phase 5)
- [ ] Implement: `src/EventLedger.Gateway/Controllers/EventsController.cs` — `POST /events`, `GET /events/{id}`, `GET /events?account={accountId}` (parses request/routes only, per [standards/backend-architecture.md](../../standards/backend-architecture.md#file-placement-table) — no validation or business logic here, both live in `Application/` from Phase 4)

### Phase 6: Account Service — Controllers

- [ ] Test: `POST /accounts/{accountId}/transactions` with a valid payload → `201` with the record (FB-9) (integration, Phase 6)
- [ ] Test: `POST /accounts/{accountId}/transactions` with a duplicate `eventId` → `200` with the original (ID-3) (integration, Phase 6)
- [ ] Test: `GET /accounts/{accountId}/balance` → `200` with the computed balance (FB-10) (integration, Phase 6)
- [ ] Test: `GET /accounts/{accountId}` → `200` with `accountId` + transaction list (FB-11) (integration, Phase 6)
- [ ] Implement: `src/EventLedger.AccountService/Controllers/AccountsController.cs` — `POST /accounts/{accountId}/transactions`, `GET /accounts/{accountId}/balance`, `GET /accounts/{accountId}`

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../standards/backend-architecture.md#test-project-layout):
`tests/EventLedger.Gateway.Tests`, `tests/EventLedger.AccountService.Tests`
— both already carry `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
from issue #3 (added because `Console.Out` redirection and static
`Log.Logger` are global state); this story's new tests inherit that
setting automatically, no new configuration needed.

Every test in Phases 2–6 above that touches persistence or the `UNIQUE`
constraint runs against a **real, file-based SQLite database — a fresh
temp file per test class, created via `EnsureCreated()` and deleted on
teardown** — never `InMemory`, per
[docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md).

### Test Cases

Listed inline within each phase above, immediately before the
implementation step(s) they verify — test-writing steps precede their
corresponding implementation steps in checkbox order throughout.

## Decisions Made

1. **Sequencing**: this plan assumes issue #3's PR
   ([#11](https://github.com/vijaykgubbala/EventLedger/pull/11)) has
   merged to `master` before `workflow-execute 2` runs.
2. **No repository interface** — `Application/` handlers inject `DbContext`
   directly (brainstorm's Approach 2, reconfirmed unchanged in the v2
   brainstorm). Idempotency correctness requires real SQLite for
   meaningful tests regardless, so a mockable repository would buy
   nothing here.
3. **Test SQLite databases**: fresh temp file per test class, deleted on teardown.
4. **Duplicate `eventId` with a different payload**: compare the fields
   each service actually stores (Gateway: `Type`/`Amount`/`Currency`/
   `EventTimestamp`; Account Service: `Type`/`Amount`); if they differ from
   the stored record, log one `Warning`-level line. Behavior is unchanged
   either way — always return the original record.
5. **Backstop `CHECK`-constraint violation** on the Account Service
   (should be unreachable given the Gateway validates first): return
   `500`, log at `Error` level, distinct from the routine `400`s a client
   can trigger directly.
6. **Scope addition**: `GET /accounts/{accountId}/balance`,
   `GET /accounts/{accountId}`, `GET /events/{id}`, and
   `GET /events?account=...` are included in this story despite not being
   explicitly listed in issue #2's original GitHub checklist.
7. **`DbContext` registration extends the existing `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()`
   methods in place**, rather than adding new, separately-chained
   extension methods — matches the design intent issue #3's own plan
   recorded ("the one call site each story adds a line to, so `Program.cs`'s
   shape never needs to change again"). Not answered live when asked
   during the v2 brainstorm; applied as the reasoned default.
8. **Handlers and the validator get explicit `AddScoped<T>()` DI
   registration** inside those same extension methods — standard ASP.NET
   Core practice, keeps dependencies visible and swappable via
   `WebApplicationFactory`'s service-override hooks in tests. Also not
   answered live; applied as the reasoned default.
9. **`Database.EnsureCreated()` requires one new line in each `Program.cs`**
   (`app.EnsureGatewayDatabaseCreated();` / the Account Service
   equivalent) — the one necessary exception to issue #3's
   "`Program.cs` never changes again" intent, since `EnsureCreated()`
   needs the built `app` to resolve a scoped `DbContext`, which no
   `WebApplicationBuilder`-scoped extension method has access to. Routed
   through a named `Infrastructure/` extension method, not inlined, to
   keep the spirit of the orchestrator rule.

### Known Constraints

- **No Polly resilience pipeline in this story.** The Gateway's call to
  the Account Service uses a plain, unwrapped `HttpClient` for now.
  Circuit breaker, timeout, and retry are issue #6's scope — this story's
  tests must not assert breaker/timeout behavior; that belongs to #6's
  test suite.
- **The Gateway's own `GET /accounts/{accountId}/balance` proxy** (as
  opposed to the Account Service's native endpoint, which *is* built in
  this story) **is out of scope** — reserved for issue #7 per its
  acceptance criteria.
- **Health checks (`GET /health` on either service) are out of scope for
  new work here** — already delivered by issue #3, nothing further needed
  in this story.
- **SQLite `decimal` storage** uses EF Core's default handling for the
  SQLite provider (`NUMERIC` affinity with precision-preserving
  serialization). Accepted as-is; revisit only if a test in Phase 2–3
  reveals precision loss on a specific value.
