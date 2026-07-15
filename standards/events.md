# Event Standards

Defines the event payload — the domain record submitted to
`POST /events` — and clarifies a naming point worth being explicit about:
this is **not** a pub/sub or message-bus "event" in the eventing/messaging
sense. There is no publisher, no subscriber, no broker, and no fan-out
anywhere in this system. "Event" here means what it means in the
assignment: a record of a financial transaction that occurred, submitted
once (or more, if retried) via a synchronous REST call. See
[../architecture/vertical-architecture.md](../architecture/vertical-architecture.md#system-shape)
for the system's full communication topology — one synchronous REST call,
full stop.

## Payload shape

```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": {
    "source": "mainframe-batch",
    "batchId": "B-9042"
  }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `eventId` | string | Yes | Unique identifier for the event — the idempotency key, enforced as a DB `UNIQUE` constraint (see [data-model.md](../architecture/data-model.md)) |
| `accountId` | string | Yes | The account this event belongs to; not validated against a pre-existing account list — see [data-model.md](../architecture/data-model.md#account-service-schema) |
| `type` | string | Yes | Must be exactly `"CREDIT"` or `"DEBIT"` |
| `amount` | number | Yes | Must be greater than `0` |
| `currency` | string | Yes | e.g. `"USD"` — stored on the Gateway's Event record; not used or stored by the Account Service (see [service-boundaries.md](service-boundaries.md)) |
| `eventTimestamp` | string (ISO 8601) | Yes | The domain time the event *occurred* — drives listing order and is independent of when it was *received* |
| `metadata` | object | No | Opaque passthrough context; stored serialized, never inspected or validated by either service |

## `eventTimestamp` vs. arrival/receipt time

`eventTimestamp` is supplied by the caller and represents when the
transaction actually happened upstream. It is deliberately distinct from
the server-assigned `received_at`/`applied_at` columns in
[data-model.md](../architecture/data-model.md), which record when this
system saw the event. Event listings (`GET /events?account=...`) are
ordered by `eventTimestamp`, never by receipt order — that's the entire
point of the out-of-order tolerance requirement: an event that occurred
earlier must sort earlier, even if it physically arrived at the API after
a later-occurring event already had.

## Anti-patterns to avoid

- **Do not treat this system's "event" as a pub/sub message.** No queue,
  topic, broker, or async fan-out belongs anywhere in this codebase — see
  [../architecture/vertical-architecture.md](../architecture/vertical-architecture.md).
- **Do not order any listing or query by receipt/insertion time.** Always
  order by `eventTimestamp` for anything user-facing that claims
  chronological order.
- **Do not add fields to the payload beyond what's listed above** without
  updating this document and the schema in
  [data-model.md](../architecture/data-model.md) in the same change — the
  payload shape, the validation rules in
  [api.md](api.md#validation-rules-post-events), and the stored schema
  must not drift from each other.
- **Do not validate or inspect the contents of `metadata`.** It is
  intentionally opaque; giving it structure or meaning would be scope this
  system doesn't need.
