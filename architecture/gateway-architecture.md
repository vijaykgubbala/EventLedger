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

1. **Validate** the request body against the rules in
   [standards/events.md](../standards/events.md) (required fields, `type` in
   `{CREDIT, DEBIT}`, `amount > 0`). Invalid requests return `400` before
   any storage or network call happens.
2. **Idempotency check.** Attempt the insert (see below) rather than a
   separate existence check — the `UNIQUE` constraint on `eventId` is the
   arbiter, not application logic.
3. **Call the Account Service**: `POST /accounts/{accountId}/transactions`
   with the event's `accountId`, `type`, `amount`, and `eventId` (so the
   Account Service can independently enforce its own idempotency — see
   [account-architecture.md](account-architecture.md)). This call goes
   through the Polly resilience pipeline described in
   [resiliency.md](resiliency.md).
4. **On confirmed success** from the Account Service, insert the Event row
   locally and return `201` with the stored record.
5. **On a duplicate `eventId`** (the Gateway's own `UNIQUE` constraint
   rejects the insert), skip the Account Service call entirely and return
   the original stored record with `200`. This is the common idempotent-
   retry path and it does not need to touch the Account Service at all,
   since the Gateway only reaches step 4 after the Account Service already
   confirmed the transaction the first time.
6. **On Account Service failure** (error response, timeout, or an open
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
- **Do not perform a separate "does this eventId exist" `SELECT` before
  inserting.** Attempt the insert and handle the `UNIQUE` violation — see
  [vertical-architecture.md](vertical-architecture.md).
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
