# Account Service Architecture

The Account Service is internal-only — it is never called by a client
directly, only by the Gateway. This document covers its internal structure;
see [vertical-architecture.md](vertical-architecture.md) for the idempotency
and balance-computation decisions it implements.

## Responsibilities

- Apply transactions to accounts, enforcing idempotency on `eventId`
  independently of the Gateway.
- Compute account balance on read.
- Serve account details and recent transaction history.
- Never talk to any other service — it has no outbound dependency on the
  Gateway or anything else.

## Endpoints

| Method | Path | Notes |
|---|---|---|
| `POST` | `/accounts/{accountId}/transactions` | Applies one transaction; idempotent on `eventId` |
| `GET` | `/accounts/{accountId}/balance` | Computed on read — see below |
| `GET` | `/accounts/{accountId}` | Account details + recent transactions |
| `GET` | `/health` | See [observability.md](observability.md) |

Request/response shapes and status codes are defined in
[standards/api.md](../standards/api.md).

## `POST /accounts/{accountId}/transactions` flow

The Account Service receives `eventId`, `type`, and `amount` from the
Gateway (the `accountId` comes from the route). It does not receive or care
about `currency`, `eventTimestamp`, or `metadata` — those are Gateway/event
concerns, not account-ledger concerns; see
[standards/events.md](../standards/events.md) for the boundary.

1. Attempt to insert a transaction row with the given `eventId`. The
   `eventId` column has a `UNIQUE` constraint (see
   [data-model.md](data-model.md)) — this is what makes the Account Service
   safely retryable independent of anything the Gateway does.
2. **On a duplicate `eventId`**, skip re-applying the transaction and return
   the original stored transaction record with `200`. Since balance is
   computed on read (never a stored counter), a duplicate never risks
   double-counting even if this were somehow reached twice — the
   `UNIQUE` constraint is still what prevents the duplicate row from
   existing in the first place.
3. **On success**, insert the row and return `201` with the stored record.

This endpoint does not validate business fields like `amount > 0` a second
time with different rules than the Gateway — the Gateway is the system's
single entry point and already rejects invalid events before they reach
here. The Account Service does still enforce its own row-level constraints
(e.g. `amount > 0` at the database level) as a backstop, per
[data-model.md](data-model.md), since it must not trust the Gateway as the
only line of defense against a malformed row landing in its own table.

## Balance computation

`GET /accounts/{accountId}/balance` computes:

```sql
SELECT COALESCE(SUM(CASE WHEN type = 'CREDIT' THEN amount ELSE 0 END), 0)
     - COALESCE(SUM(CASE WHEN type = 'DEBIT'  THEN amount ELSE 0 END), 0)
FROM transactions
WHERE account_id = @accountId;
```

This is a live aggregate over the account's transaction rows, computed on
every request — never a stored running total. See
[vertical-architecture.md](vertical-architecture.md#core-decision-balance-computed-on-read)
for why this is required for out-of-order correctness.

## Trace propagation (inbound)

The Account Service receives the `traceparent` header on the incoming
request from the Gateway. ASP.NET Core's OpenTelemetry instrumentation picks
this up automatically and continues the same trace, so spans from both
services appear as one connected trace. The service logs the trace ID via
Serilog's `LogContext` enrichment on every request — see
[observability.md](observability.md).

## Anti-patterns to avoid

- **Do not call the Gateway, or any other service, from here.** The Account
  Service has zero outbound dependencies by design; it must remain
  independently runnable and testable in isolation.
- **Do not store balance as a column that gets incremented/decremented per
  transaction.** Compute it from `SUM(CREDIT) − SUM(DEBIT)` on every read.
- **Do not trust the Gateway's validation as the only safeguard.** Enforce
  `amount > 0` and a valid `type` at the database/model level here too — a
  bug in the Gateway must not be able to corrupt account data.
- **Do not accept or store `currency`, `eventTimestamp`, or `metadata`.**
  Those belong to the event record on the Gateway, not the account ledger —
  see [standards/service-boundaries.md](../standards/service-boundaries.md).
- **Do not manually read or forward the `traceparent` header.** Rely on the
  ASP.NET Core OpenTelemetry instrumentation to pick it up automatically.
