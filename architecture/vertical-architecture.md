# Vertical Architecture

This is the top-level architecture document for Event Ledger. It defines system
topology and the decisions that shape every other document in `architecture/`.
Read this first; other documents assume it and link back here instead of
restating it.

## System shape

Event Ledger is a **2-service, solo-contributor, local/offline system**. There
is no cloud infrastructure, no frontend, and no multi-tenancy. Both services
run as independent local processes (or containers via Docker Compose) on a
single machine. Every design decision in this repository is scaled to that
reality: a pattern is only adopted if it earns its complexity for a two-service
system with one operator, not because it is standard practice at a larger
scale.

```
                          ┌───────────────────────┐
Client (curl/Postman) ──▶ │  Event Gateway API    │
                          │  (public-facing)      │
                          └──────────┬────────────┘
                                     │ REST (sync), traceparent propagated
                                     ▼
                          ┌───────────────────────┐
                          │  Account Service      │
                          │  (internal)           │
                          └───────────────────────┘
```

- **Event Gateway** is the only service exposed to clients. It owns event
  intake, validation, idempotent storage of event records, and orchestrating
  the call to the Account Service.
- **Account Service** is internal-only — never called directly by a client,
  only by the Gateway. It owns account balance state and transaction history.
- Communication between the two is **synchronous REST over HTTP**, one call
  per event submission (`Gateway → Account Service`). There is no message
  broker, queue, or event bus anywhere in this system — see
  [standards/events.md](../standards/events.md) for why the "event" in
  "Event Ledger" is a domain record, not a pub/sub message.

Per-service topology, endpoints, and internals are covered in
[gateway-architecture.md](gateway-architecture.md) and
[account-architecture.md](account-architecture.md).

## Why two services, and no more

The assignment requires the Gateway and Account Service to be independently
runnable processes with independently owned databases. That is the entire
scope of "distribution" this system has. There is no justification for
splitting further (e.g. a separate validation service, a separate query
service) — every additional network hop is a new failure mode to design
around, for a take-home-scale system with no load or team-ownership pressure
that would justify it. Two services is the floor set by the requirements and
the ceiling justified by the problem.

## Core decision: confirm-before-persist, no outbox

**The Gateway persists an Event row only after the Account Service call has
been confirmed successful.** There is no outbox table, no "pending" event
status, and no background publisher retrying unconfirmed writes.

Sequence for `POST /events` on the Gateway:

1. Validate the request body (see [standards/api.md](../standards/api.md)).
2. Check local storage for an existing row with this `eventId`. If found,
   return the original stored record (see idempotency below) — the Account
   Service is not called again.
3. Call `POST /accounts/{accountId}/transactions` on the Account Service.
4. **Only if that call returns a confirmed success** does the Gateway insert
   the Event row locally, and only then does it return success to the client.
5. If the Account Service call fails, times out, or the circuit is open, the
   Gateway returns an error to the client (see
   [resiliency.md](resiliency.md)) and **persists nothing**. No partial or
   pending Event row is ever written.

### Why not an outbox / pending-state pattern

An outbox (write Event as "pending" locally, publish asynchronously, mark
"confirmed" later) exists to solve a specific problem: making a local write
and a remote call atomic when the remote call cannot be trusted to be safely
retried, or when the caller cannot afford to block on the remote call. Neither
condition holds here:

- The Account Service is **independently idempotent on `eventId`** (see
  below), so the Gateway retrying — or a client retrying after a Gateway
  timeout — is always safe. There is nothing for an outbox to protect against
  that idempotency doesn't already cover.
- This is a synchronous REST API per the assignment's constraints; the client
  is already blocking on the Gateway's response, so blocking the Gateway on
  the Account Service call adds no new latency category — it's the same
  request.
- An outbox introduces a background publisher process, a pending/confirmed
  state machine, and a reconciliation story for stuck "pending" rows — real
  operational surface for a solo-run local system that has no uptime
  requirement to justify it.

Net effect: confirm-before-persist keeps the Gateway's local Event table as an
accurate mirror of "transactions the Account Service actually accepted,"
with no eventual-consistency window and no extra moving parts.

## Core decision: idempotency via DB-level UNIQUE constraint

`eventId` is enforced as a **database-level `UNIQUE` constraint** on the
relevant table in *both* services (Gateway's Event table, Account Service's
transaction table) — not an application-level "check if it exists, then
insert" sequence.

Check-then-insert has a race: two concurrent requests with the same
`eventId` can both pass the existence check before either has committed its
insert, producing two rows (or a corrupted balance) for one logical event.
A `UNIQUE` constraint makes the database the single arbiter of "does this
event already exist," so the race is closed at the storage layer regardless
of how many concurrent requests arrive. See
[data-model.md](data-model.md) for the exact constraint definitions and
[standards/api.md](../standards/api.md) for the request-handling flow that
catches the resulting constraint-violation and turns it into a duplicate
response.

**On a recognized duplicate, both services return the original stored
record with `200`/`201` — never `409`.** The assignment's contract is that a
duplicate submission is not a client error; it is the same logical operation
being observed twice, and the correct response is "here is what already
happened," not "you did something wrong." Returning the original record
(rather than a bare success) lets a client verify the duplicate matched what
it expected to have submitted.

## Core decision: balance computed on read

Account balance is **never a stored running counter**. It is computed on
every read as `SUM(amount WHERE type = CREDIT) − SUM(amount WHERE type =
DEBIT)` over the account's transaction rows.

This follows directly from the out-of-order requirement: events can arrive
with an earlier `eventTimestamp` after one with a later `eventTimestamp` has
already been applied. A stored running counter has no way to represent
"insert this transaction into the middle of a sequence already applied" — it
can only append. Computing the sum from the full row set on every read is
correct under any arrival order, because summation doesn't care about
insertion order. The cost is an aggregate query per balance read instead of a
field lookup; at this system's scale (a take-home's worth of data volume,
no concurrent-load requirement) that cost is not worth trading correctness
for. See [data-model.md](data-model.md) for the schema this depends on and
[account-architecture.md](account-architecture.md) for the query.

Event *listings* (`GET /events?account=...`) are ordered by `eventTimestamp`
(the domain time the event occurred), not by insertion/arrival order — the
same out-of-order requirement applied to reads instead of aggregation.

## Cross-cutting concerns, by owning document

| Concern | Owning document |
|---|---|
| Per-service endpoints, request/response flow | [gateway-architecture.md](gateway-architecture.md), [account-architecture.md](account-architecture.md) |
| Schema, constraints, SQLite/EF Core setup | [data-model.md](data-model.md) |
| Tracing, structured logging, metrics | [observability.md](observability.md) |
| Circuit breaker, timeout, retry on the Gateway→Account call | [resiliency.md](resiliency.md) |
| Local/Docker Compose run topology | [deployment-architecture.md](deployment-architecture.md) |
| Request/response contracts, HTTP status codes | [standards/api.md](../standards/api.md) |
| The event payload shape | [standards/events.md](../standards/events.md) |
| Per-service must-have/must-not rules | [standards/service-boundaries.md](../standards/service-boundaries.md) |

## Anti-patterns to avoid

- **Do not add an outbox table, message queue, or background publisher.**
  The Account Service's idempotency already makes retries safe; an outbox
  solves a problem this system doesn't have and adds a pending-state
  reconciliation burden with no operator to reconcile it.
- **Do not implement idempotency as an application-level "SELECT then
  INSERT if not found" check.** It races under concurrent duplicate
  submissions. Idempotency must be enforced by a database `UNIQUE`
  constraint on `eventId`.
- **Do not return `409 Conflict` for a duplicate `eventId`.** A recognized
  duplicate is not a conflict — return the original stored record with
  `200`/`201`.
- **Do not store balance as a running counter field.** It cannot be correct
  under out-of-order arrival. Balance must be computed on read from the full
  transaction row set.
- **Do not let the Gateway persist an Event before the Account Service call
  is confirmed successful.** A "pending" or optimistically-written Event row
  can drift from what the Account Service actually accepted.
- **Do not introduce a third service, an API gateway/reverse proxy layer,
  or a service mesh.** Two services, direct REST, is the whole topology this
  problem calls for.
- **Do not reach for Kubernetes, Terraform, or any cloud-provider
  infrastructure.** This system runs locally or via Docker Compose only —
  see [deployment-architecture.md](deployment-architecture.md).
