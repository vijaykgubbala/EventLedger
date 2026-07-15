# Naming Conventions

Applies to both services. Where .NET/C# has an established convention,
follow it — the goal here is documenting the handful of decisions specific
to this codebase, not restating the language's style guide.

## C# code

- **Namespaces**: `EventLedger.Gateway.*` and `EventLedger.AccountService.*`,
  mirroring the folder-based layers in
  [backend-architecture.md](backend-architecture.md) (e.g.
  `EventLedger.Gateway.Domain`, `EventLedger.AccountService.Application`).
- **Types, methods, properties**: `PascalCase` (standard .NET convention).
- **Local variables, parameters**: `camelCase` (standard .NET convention).
- **Async methods**: suffixed `Async` (e.g. `SubmitEventAsync`), per
  standard .NET convention — applies to essentially every I/O-touching
  method in this codebase, since both services are built on EF Core and
  `HttpClient`.
- **Interfaces**: prefixed `I` (e.g. `IEventRepository`), standard .NET
  convention.

## Domain vocabulary — use these terms consistently, in code and docs

| Term | Meaning | Not to be confused with |
|---|---|---|
| Event | A submitted transaction record (`POST /events` payload) | A pub/sub message — see [events.md](events.md) |
| Transaction | The Account Service's applied record of one event | — |
| Account | An implicit identity (`accountId` string); not a stored entity — see [data-model.md](../architecture/data-model.md) |
| Balance | The computed-on-read `SUM(CREDIT) − SUM(DEBIT)` for an account | A stored counter field (this system has none) |
| `eventId` | The idempotency key, shared verbatim between both services' tables | `id` (the internal DB surrogate key, service-local, never exposed cross-service) |
| `eventTimestamp` | Caller-supplied domain time the event occurred | `received_at`/`applied_at` (server-assigned receipt time) |

Keep `Event` (the Gateway-owned submission record) and `Transaction` (the
Account-Service-owned applied record) as distinct terms in code, comments,
and docs — they represent the same real-world occurrence but are two
different rows in two different databases, owned by two different
services, and collapsing the terminology invites collapsing the boundary
between them.

## Database

- **Tables**: `snake_case`, plural (`events`, `transactions`) — SQLite/EF
  Core convention used consistently across both services' schemas.
- **Columns**: `snake_case` (`event_id`, `account_id`, `event_timestamp`).
- EF Core entity classes stay `PascalCase` (`Event`, `Transaction`) per C#
  convention above; the `snake_case` mapping is a configuration concern
  handled once in each service's EF Core model configuration, not
  something reflected back into the C# class or property names.

## HTTP

- **Routes**: `kebab-case` is not needed here since no route segment in
  this API is multi-word — routes follow the assignment's literal paths
  (`/events`, `/accounts/{accountId}/transactions`, etc.) verbatim; don't
  rename them for "consistency" with some other convention.
- **JSON payload fields**: `camelCase` (`eventId`, `accountId`,
  `eventTimestamp`), matching the assignment's example payload exactly.

## Anti-patterns to avoid

- **Do not use `Event` and `Transaction` interchangeably.** They are
  different records, owned by different services — see the vocabulary
  table above.
- **Do not rename the assignment's literal route paths or JSON field
  names** (e.g. `eventId` → `event_id` in the wire payload, or
  `/accounts/{accountId}/transactions` → some other path). The API
  contract is fixed by the assignment; naming preferences don't override
  it.
- **Do not mix `snake_case` database columns into C# as `snake_case`
  properties.** Keep the EF Core mapping layer as the single place that
  translates between the two.
