# Data Model

Both services persist to **SQLite via EF Core, file-based** — one `.db` file
per service, created and migrated independently. Neither `:memory:` mode nor
EF Core's `InMemory` provider is used anywhere in this system. See
[vertical-architecture.md](vertical-architecture.md) for why: the
idempotency guarantee depends on a real, enforced `UNIQUE` constraint, and
EF Core's `InMemory` provider does not enforce relational constraints
(including `UNIQUE`) — it would silently defeat the exact guarantee this
system is built around. A file-based SQLite database also means state
survives a service restart, which matters for a system meant to be run and
inspected locally rather than spun up fresh for every test.

Each service owns its database file exclusively. There is no shared
database, schema, or connection string between the Gateway and the Account
Service — see
[standards/service-boundaries.md](../standards/service-boundaries.md).

## Event Gateway schema

**`events` table**

| Column | Type | Constraints |
|---|---|---|
| `id` | `INTEGER` | Primary key, autoincrement (internal surrogate key) |
| `event_id` | `TEXT` | **`UNIQUE`, `NOT NULL`** — the idempotency key |
| `account_id` | `TEXT` | `NOT NULL` |
| `type` | `TEXT` | `NOT NULL`, `CHECK (type IN ('CREDIT', 'DEBIT'))` |
| `amount` | `DECIMAL` | `NOT NULL`, `CHECK (amount > 0)` |
| `currency` | `TEXT` | `NOT NULL` |
| `event_timestamp` | `TEXT` (ISO 8601) | `NOT NULL` — the domain time the event occurred; drives listing order |
| `metadata_json` | `TEXT` | Nullable — the optional `metadata` object, stored as serialized JSON since it's opaque passthrough, not a queried field |
| `received_at` | `TEXT` (ISO 8601) | `NOT NULL` — server-assigned insert time, for operator debugging only; never used for ordering or balance logic |

Indexes:
- `UNIQUE` index on `event_id` (the idempotency constraint itself).
- Index on `(account_id, event_timestamp)` to support
  `GET /events?account=...` ordered by `eventTimestamp` without a full
  table scan.

## Account Service schema

**`transactions` table**

| Column | Type | Constraints |
|---|---|---|
| `id` | `INTEGER` | Primary key, autoincrement |
| `event_id` | `TEXT` | **`UNIQUE`, `NOT NULL`** — the idempotency key |
| `account_id` | `TEXT` | `NOT NULL` |
| `type` | `TEXT` | `NOT NULL`, `CHECK (type IN ('CREDIT', 'DEBIT'))` |
| `amount` | `DECIMAL` | `NOT NULL`, `CHECK (amount > 0)` |
| `applied_at` | `TEXT` (ISO 8601) | `NOT NULL` — server-assigned insert time, for operator debugging only |

Indexes:
- `UNIQUE` index on `event_id`.
- Index on `account_id` to support balance aggregation and recent-
  transactions lookups without a full table scan.

There is no separate `accounts` table. An account is an implicit identity —
`account_id` is just a string that shows up on transaction rows; nothing
about this system requires pre-registering accounts, and the assignment
does not ask for account management. `GET /accounts/{accountId}` and
`GET /accounts/{accountId}/balance` both derive their response entirely
from the `transactions` rows for that `account_id`.

## Idempotency enforcement

Both `UNIQUE` constraints above are the actual enforcement mechanism for
"submitting the same `eventId` twice must not create a duplicate or alter
state." The request-handling flow attempts the insert and catches the
resulting constraint-violation exception (SQLite raises a distinct
`SQLITE_CONSTRAINT` error for `UNIQUE` violations, which the EF Core
`DbUpdateException` wraps) to detect a duplicate and fetch+return the
original row. See
[vertical-architecture.md](vertical-architecture.md#core-decision-idempotency-via-db-level-unique-constraint)
for the rationale and
[standards/api.md](../standards/api.md) for how that maps to HTTP status
codes.

## Anti-patterns to avoid

- **Do not use SQLite `:memory:` mode or EF Core's `InMemory` provider for
  either service, including in tests that assert idempotency behavior.**
  `InMemory` does not enforce `UNIQUE` constraints, so it cannot actually
  verify the guarantee this system depends on. Tests that need to exercise
  the constraint must run against a real (file-based, possibly temp-file)
  SQLite database.
- **Do not add a stored `balance` column anywhere.** Balance is always
  computed from `transactions` rows on read — see
  [vertical-architecture.md](vertical-architecture.md).
- **Do not let the two services share a database file, connection string,
  or schema.** Each service's schema is private to that service.
- **Do not add an `accounts` table "for completeness."** Nothing in the
  requirements calls for account registration/management, and adding one
  would need to be kept in sync with `transactions` for no behavioral
  benefit.
- **Do not skip the `CHECK` constraints on `type` and `amount` at the
  database level.** Application-level validation is the primary defense,
  but the database constraint is the backstop against any code path that
  bypasses it — see
  [account-architecture.md](account-architecture.md#anti-patterns-to-avoid).
