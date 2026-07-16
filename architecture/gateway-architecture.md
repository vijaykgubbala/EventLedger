# Event Gateway Architecture

The Event Gateway is the public-facing service. It is the only service a
client ever talks to. This document covers its internal structure and
request handling; see [vertical-architecture.md](vertical-architecture.md)
for why it behaves the way it does (confirm-before-persist, idempotency),
and [standards/backend-architecture.md](../standards/backend-architecture.md)
for the concrete project layout.

## Responsibilities

- Accept and validate incoming transaction events (`POST /events`).
- Enforce idempotency on `eventId` for its own Event table.
- Call the Account Service to apply the transaction, and only persist the
  Event locally once that call is confirmed successful (see
  [vertical-architecture.md](vertical-architecture.md#core-decision-confirm-before-persist-no-outbox)).
- Serve event reads (`GET /events/{id}`, `GET /events?account=...`) entirely
  from its own local data — these must keep working even if the Account
  Service is down.
- Proxy balance reads to the Account Service and surface a clear error if
  it's unreachable.
- Propagate a trace context to the Account Service on every outbound call.

## Endpoints

| Method | Path | Depends on Account Service? | Notes |
|---|---|---|---|
| `POST` | `/events` | Yes (synchronously, in the request path) | Only persists after confirmed success; see below |
| `GET` | `/events/{id}` | No | Local read only |
| `GET` | `/events?account={accountId}` | No | Local read only, ordered by `eventTimestamp` |
| `GET` | `/health` | No (reports Account Service reachability as a diagnostic, does not block on it) | See [observability.md](observability.md) |

Request/response shapes and status codes are defined in
[standards/api.md](../standards/api.md). The event payload shape is defined
in [standards/events.md](../standards/events.md).

## `POST /events` flow

The Gateway's idempotency handling is **two-stage**, precisely because of
confirm-before-persist: the local insert can only happen *after* the
Account Service confirms, so a separate, earlier check is needed to avoid
calling the Account Service again on an already-known duplicate. Neither
stage alone is the full idempotency guarantee — stage 1 is a performance
optimization for the common sequential-retry case; stage 2 (the `UNIQUE`
constraint at insert time) is what actually closes the concurrent-race
case and is the real safety mechanism.

1. **Validate** the request body against the rules in
   [standards/events.md](../standards/events.md) (required fields, `type` in
   `{CREDIT, DEBIT}`, `amount > 0`). Invalid requests return `400` before
   any storage or network call happens.
2. **Fast-path duplicate check.** `SELECT` the Gateway's own `events` table
   for this `eventId`. If found, return the stored record immediately with
   `200` — the Account Service is **not** called. This is the common
   idempotent-retry path (a client resubmitting after already getting a
   success) and it's purely an optimization: skipping it would not break
   correctness, only waste a redundant Account Service call.
3. **If not found locally**, call the Account Service:
   `POST /accounts/{accountId}/transactions` with the event's `accountId`,
   `type`, `amount`, and `eventId` (so the Account Service can independently
   enforce its own idempotency — see
   [account-architecture.md](account-architecture.md)). This call goes
   through the Polly resilience pipeline described in
   [resiliency.md](resiliency.md).
4. **On confirmed success** from the Account Service (a fresh apply, or the
   Account Service's own duplicate response), attempt to `INSERT` the Event
   row locally:
   - **Insert succeeds** → return `201` with the new record.
   - **Insert fails on the `UNIQUE` constraint** (a concurrent duplicate
     request raced ahead and inserted first, between this request's step 2
     check and this step) → `SELECT` and return the already-committed
     record with `200`, not the data this request tried to insert. This is
     the actual idempotency guarantee — see
     [vertical-architecture.md](vertical-architecture.md#core-decision-idempotency-via-db-level-unique-constraint)
     and
     [docs/patterns/2026-07-15-idempotency-key-race.md](../docs/patterns/2026-07-15-idempotency-key-race.md).
5. **On Account Service failure** (error response, timeout, or an open
   circuit — see [resiliency.md](resiliency.md)), persist nothing and
   return `503` with a message indicating the Account Service is
   unavailable. See
   [resiliency.md](resiliency.md#graceful-degradation) for the full
   degradation matrix.

## Trace propagation (outbound)

The Gateway is where a request's trace context originates. ASP.NET Core's
OpenTelemetry instrumentation creates the root span for the incoming
request; the `HttpClient` instrumentation automatically attaches the W3C
`traceparent` header to the outbound call to the Account Service. No manual
header plumbing is required or should be added — see
[observability.md](observability.md).

## Anti-patterns to avoid

- **Do not call the Account Service before validating the request body.**
  Validation failures must never produce a network call.
- **Do not treat the fast-path `SELECT` in step 2 as the idempotency
  guarantee.** It only catches a duplicate that arrived *after* an earlier
  request already completed; it cannot see a concurrent request still
  in-flight. The final insert (step 4) must still be attempt-insert /
  catch-`UNIQUE`-violation, never "the earlier `SELECT` found nothing, so
  skip straight to `INSERT` without handling a possible race." See
  [vertical-architecture.md](vertical-architecture.md).
- **Do not skip the fast-path `SELECT` (step 2) and call the Account
  Service on every request "since the final insert will catch duplicates
  anyway."** That's correct but wasteful — every sequential retry would
  re-call the Account Service for no reason. Keep both stages.
- **Do not call the Account Service again on a recognized duplicate
  `eventId`.** The first successful call already applied the transaction;
  calling again is redundant traffic in the best case and a correctness
  risk if the Account Service's own idempotency has an edge case.
- **Do not make `GET /events/{id}` or `GET /events?account=...` depend on
  the Account Service in any way.** They must be answerable from purely
  local data, including when the Account Service is completely down.
- **Do not manually forward or construct trace headers.** Use the
  OpenTelemetry `HttpClient` instrumentation so `traceparent` propagation is
  automatic; hand-rolled header code is exactly the kind of thing that
  silently breaks when the instrumentation configuration changes.
