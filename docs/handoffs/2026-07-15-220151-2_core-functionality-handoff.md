---
issue: 2
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/2
branch: 2_core-functionality
base: master
plan: docs/plans/2_core-functionality-plan.md
---

# Handoff: Story 1: Core functionality — idempotency, out-of-order tolerance, balance computation, validation

## Release Notes

This PR implements the core transaction-processing path for both services: the Gateway's `POST /events` idempotent submission flow, the Account Service's `POST /accounts/{accountId}/transactions` idempotent apply flow, balance computation, and the read endpoints on both sides (`GET /events/{id}`, `GET /events?account=...`, `GET /accounts/{accountId}`, `GET /accounts/{accountId}/balance`).

The design follows the two-stage idempotency pattern already recorded in `architecture/gateway-architecture.md`: a fast-path local `SELECT` catches the common sequential-retry case without calling the Account Service again, and a real database `UNIQUE` constraint (not an application-level check) is the actual safety net against concurrent duplicate submissions. Both services independently enforce their own idempotency on `eventId` — the Account Service never trusts the Gateway's validation as sufficient, per `standards/service-boundaries.md`. Balance is always computed live as `SUM(CREDIT) − SUM(DEBIT)` over the account's rows, never a stored running counter, which is what makes out-of-order event arrival safe: the balance comes out correct regardless of the order transactions were applied in.

Work proceeded phase by phase (domain types → persistence → Application-layer handlers → Controllers, for each service) with genuine TDD discipline — every test was confirmed red before the corresponding code was written. Along the way, three real EF Core/SQLite provider bugs were found and fixed, each documented as a pattern for future work in this codebase (see `docs/patterns/`):

1. A raw `CHECK` constraint on a `decimal` column needs an explicit `CAST(... AS REAL)`, or SQLite's `TEXT`-affinity comparison rules make it silently non-functional.
2. EF Core's SQLite provider cannot translate `SUM`/`OrderBy` over `decimal`/`DateTimeOffset` columns into SQL at all — both `BalanceQueryHandler` and the various listing/ordering handlers materialize rows first and aggregate/order client-side.
3. Neither `DbContext` originally mapped its columns to the documented `snake_case` names (EF Core defaulted to the C# `PascalCase` property names) — caught via a schema dump, fixed with explicit `HasColumnName(...)` calls, and guarded with a regression test in both projects.

After the six implementation phases, a 4-agent `/simplify` pass and a 5-agent `workflow-review` pass (both documented in `docs/reviews/2_core-functionality.json`) found and fixed one critical issue: `AccountsController.ApplyTransaction` dereferenced unvalidated request fields with null-forgiving operators, so a malformed request to the internal Account Service endpoint crashed with an unhandled exception (and, in the default `Development` environment, leaked a stack trace) instead of returning a structured `400`. That's now guarded, along with 11 other warning/suggestion-level findings (missing `AsNoTracking()`, an undisposed `HttpResponseMessage`, a duplicated balance formula, several test-coverage gaps, and doc drift between `standards/api.md` and the delivered `404`-vs-`200` behavior on the Account Service's `GET` endpoints).

**Known, deliberate scope boundaries** (not gaps — see the plan's "Known Constraints"): the Gateway calls the Account Service via a plain, unwrapped `HttpClient` — no Polly resilience pipeline yet, that's issue #6. The Gateway's own `GET /accounts/{accountId}/balance` proxy endpoint is issue #7's scope, not this one.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| Idempotency (both services' `UNIQUE`-constraint insert-or-fetch paths) | Large — this is the system's core correctness guarantee | `SubmitEventHandler.cs`, `ApplyTransactionHandler.cs`; the `SqliteExtendedErrorCode`-based UNIQUE-vs-CHECK distinction | Real file-based SQLite throughout (never `InMemory`), concurrent `Task.WhenAll` tests against two independent `DbContext` instances backed by the same file, run repeatedly with no flakiness observed |
| Balance computation | Large — a wrong balance is the worst possible bug for a financial ledger | `BalanceQueryHandler.cs`, `TransactionRecordExtensions.ComputeBalance()` | Mixed, zero, all-credit, all-debit unit tests; a full-flow test asserting correctness after mixed CREDIT/DEBIT events submitted out of `eventTimestamp` order |
| Request validation | Medium — the one critical review finding was here | `EventValidator.cs` (Gateway), the new guard clause in `AccountsController.cs` (Account Service) | Controller-level `400` tests for missing-field, `amount<=0`, and invalid-`type`, on both services |
| Persistence schema | Medium — column-naming and CHECK-constraint bugs already found and fixed once here | `GatewayDbContext.cs`, `AccountDbContext.cs` | Schema-dump regression tests (`Schema_UsesSnakeCaseColumnNames`) and an unmapped-enum-value regression test in both `*DbContextTests.cs` |
| Cross-service HTTP call (Gateway → Account Service) | Small — no Polly yet, by design (issue #6) | `SubmitEventHandler.cs`'s `HttpRequestException`/non-success-status handling | One real full-flow integration test (`GatewayToAccountServiceFullFlowTests.cs`) wiring two in-process `WebApplicationFactory` hosts together via `TestServer.CreateHandler()` |

## Test Coverage

### Planned vs Actual

| Planned Test | Status | Notes |
|---|---|---|
| Duplicate `eventId` insert throws `DbUpdateException` (both entity types) | written | `GatewayDbContextTests`, `AccountDbContextTests` |
| `Amount <= 0` insert throws `DbUpdateException` (`CHECK` constraint) | written | Required an unplanned fix: raw `CHECK` SQL needed `CAST(... AS REAL)` |
| `ApplyTransactionHandler`/`SubmitEventHandler`: new event, duplicate, concurrent duplicate, mismatched-payload warning, backstop-`CHECK`-violation | written | All five per handler, both services |
| `BalanceQueryHandler`: mixed, zero-transaction | written | Extended during review to add all-credit/all-debit |
| `AccountDetailsHandler`: accountId + transaction list | written | |
| `EventValidator`: missing field, `amount<=0`, invalid `type`, valid payload | written | Extended during review to add metadata-must-be-object |
| Controller-level `POST`/`GET` happy paths and `400`/`404` | written | Extended during review to add controller-level missing-field/invalid-type `400`s (previously only unit-tested) |
| One full Gateway→Account-Service integration test against real (non-mocked) instances | written | `GatewayToAccountServiceFullFlowTests.cs`; extended during review with an out-of-order + balance-correctness case |
| (unplanned) `EventQueryHandler` (Application-layer read handler for the two `GET /events...` routes) | added | Not itemized in the original Phase 4/5 plan text — required to avoid direct `DbContext` usage in `Controllers/`, per `standards/backend-architecture.md` |
| (unplanned) Schema/column-naming and unmapped-enum-value regression tests | added | Caught a real bug (see Release Notes) |

### What's Not Tested

Polly/circuit-breaker behavior and `traceparent` propagation are explicitly out of scope for this story (issues #6 and #4 respectively) and have no tests here by design. A second full-flow test covering duplicate-`eventId` resubmission through the real two-service wiring was considered during review and deliberately skipped (`docs/reviews/2_core-functionality.json`, finding F11) — that path is already thoroughly covered by stub-backed tests in both services, and a second dual-host integration test wasn't judged worth its runtime cost for the incremental coverage. `Program.cs` bootstrapping and DTO records are untested, consistent with this project's stated policy of not chasing 100% coverage on generated/trivial code.
