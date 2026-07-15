# Service Boundaries

Concrete must-have / must-not rules for what belongs in each service. This
is the enforcement-level companion to
[../architecture/vertical-architecture.md](../architecture/vertical-architecture.md);
that document explains *why* the system is shaped this way, this document
is the checklist for keeping a change on the right side of the boundary.

## Event Gateway

| Must | Must not |
|---|---|
| Own the `events` table and its `event_id` `UNIQUE` constraint | Share a database, connection string, or schema with the Account Service |
| Validate the full event payload (required fields, `type`, `amount > 0`) before any network call | Call the Account Service before validation passes |
| Persist an Event row only after the Account Service confirms the transaction | Persist a "pending" or optimistic Event row before confirmation |
| Serve `GET /events/{id}` and `GET /events?account=...` from local data only | Make either of those endpoints depend on the Account Service being reachable |
| Call the Account Service through the Polly resilience pipeline | Issue an unwrapped, un-timed-out `HttpClient` call to the Account Service |
| Propagate `traceparent` automatically via OpenTelemetry `HttpClient` instrumentation | Manually construct or forward trace headers |
| Pass `eventId`, `accountId`, `type`, `amount` to the Account Service | Expect the Account Service to accept or store `currency`, `eventTimestamp`, or `metadata` |

## Account Service

| Must | Must not |
|---|---|
| Own the `transactions` table and its `event_id` `UNIQUE` constraint | Share a database, connection string, or schema with the Gateway |
| Enforce its own idempotency on `eventId`, independent of the Gateway | Trust the Gateway's idempotency check as sufficient and skip its own `UNIQUE` constraint |
| Compute balance as `SUM(CREDIT) − SUM(DEBIT)` on every read | Store balance as a running counter field |
| Enforce `amount > 0` and a valid `type` at the database level as a backstop | Rely solely on the Gateway's validation for data integrity |
| Remain callable and testable with zero outbound HTTP dependencies | Call the Gateway, or any other service, for any reason |
| Log the incoming `traceparent`'s trace ID via `Enrich.FromLogContext()` | Read or parse the `traceparent` header manually |

## Why these are enforced this strictly

Both services are required by the assignment to be **independently
runnable processes** with **no shared database or in-process state**. The
tables above are what that requirement looks like at the level of an
individual code change — a PR that adds a foreign-key-style dependency
from one service's schema to the other's, or that makes the Account
Service call back into the Gateway, violates the assignment's constraints
directly, not just this repo's style preference.

## Anti-patterns to avoid

- **Do not add a shared library that both services import for
  domain/business logic beyond truly generic, non-domain utilities** (e.g.
  a shared HTTP client factory is fine; a shared "Transaction" domain
  class that both services depend on is not — that recreates the coupling
  the two-database rule was meant to prevent, just one layer up).
- **Do not let either service reach into the other's database file
  directly**, even for read-only diagnostics. If a service needs data
  owned by the other, it goes through that service's HTTP API — for the
  Gateway calling the Account Service, that's the only cross-service call
  this system has; the Account Service should never need to originate one.
- **Do not add fields to the Account Service's `transactions` table that
  only the Gateway cares about** (`currency`, `metadata`, etc.) just
  because they're available on the incoming event. If the Account Service
  doesn't use a field for balance or transaction-history purposes, it
  doesn't store it.
