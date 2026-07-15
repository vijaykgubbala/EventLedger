# API Standards

Request/response conventions shared by both services' REST APIs. Endpoint
lists and per-service flow live in
[../architecture/gateway-architecture.md](../architecture/gateway-architecture.md)
and
[../architecture/account-architecture.md](../architecture/account-architecture.md);
this document is the contract shape and status-code convention those flows
implement.

## Status codes

| Situation | Status | Applies to |
|---|---|---|
| New record created successfully | `201 Created` | `POST /events`, `POST /accounts/{id}/transactions` |
| Duplicate `eventId` recognized — original record returned | `200 OK` | Both `POST` endpoints |
| Successful read | `200 OK` | All `GET` endpoints |
| Request body fails validation (missing field, `amount <= 0`, unknown `type`) | `400 Bad Request` | `POST /events` |
| Requested resource does not exist | `404 Not Found` | `GET /events/{id}`, `GET /accounts/{id}`, `GET /accounts/{id}/balance` |
| Account Service unreachable, timed out, or circuit open | `503 Service Unavailable` | Gateway only: `POST /events`, balance-related reads |
| Unhandled server-side fault | `500 Internal Server Error` | Any endpoint — should be rare; an Account Service outage is `503`, not `500` |

**A recognized duplicate `eventId` is never `409 Conflict`.** See
[../architecture/vertical-architecture.md](../architecture/vertical-architecture.md#core-decision-idempotency-via-db-level-unique-constraint)
for why — a duplicate is the same logical operation observed twice, not a
conflicting one.

## Error response shape

Every non-2xx response body is a JSON object with a consistent shape so
clients can parse errors uniformly:

```json
{
  "error": "validation_error",
  "message": "amount must be greater than 0",
  "details": {
    "field": "amount"
  }
}
```

`error` is a short machine-readable code (`validation_error`,
`not_found`, `account_service_unavailable`, etc.); `message` is a
human-readable explanation; `details` is optional and situational (e.g.
which field failed validation). This satisfies the assignment's
requirement for "meaningful error messages with appropriate HTTP status
codes" — the status code carries the category, the body carries the
specifics.

## Success response shape

Both `POST /events` and `POST /accounts/{accountId}/transactions` return
the full stored record on success (whether newly created or a recognized
duplicate) — not just an ID or a bare acknowledgement. This is what lets a
duplicate-submission response ("here is what already happened") actually
be useful to the caller, per
[../architecture/vertical-architecture.md](../architecture/vertical-architecture.md).

`GET` endpoints return the resource(s) directly; list endpoints
(`GET /events?account=...`) return a JSON array, ordered by
`eventTimestamp` ascending.

## Validation rules (`POST /events`)

Enforced by the Gateway before any Account Service call, per
[events.md](events.md) for the full payload definition:

- `eventId`, `accountId`, `type`, `amount`, `currency`, `eventTimestamp`
  are all required; a missing field is a `400`.
- `type` must be exactly `"CREDIT"` or `"DEBIT"` (case-sensitive); any
  other value is a `400`.
- `amount` must be a number strictly greater than `0`; zero or negative is
  a `400`.
- `eventTimestamp` must be a valid ISO 8601 timestamp.
- `metadata`, if present, is passed through opaquely — no validation is
  performed on its contents beyond it being a JSON object.

## Anti-patterns to avoid

- **Do not return `409 Conflict` for a duplicate `eventId`.** Use `200`
  with the original record.
- **Do not return `500` for an Account Service outage.** Use `503`.
- **Do not return a bare `{"status": "ok"}` or an ID-only body from a
  `POST` endpoint.** Return the full stored record so idempotent-retry
  responses are actually useful to the caller.
- **Do not invent per-endpoint error body shapes.** Every error response
  uses the same `{error, message, details?}` shape regardless of which
  endpoint produced it.
- **Do not validate `amount`/`type` differently between the Gateway and
  the Account Service "for defense in depth" with different rules in each
  place.** The Account Service's database-level `CHECK` constraints (see
  [data-model.md](../architecture/data-model.md)) are the backstop; they
  should express the same rules as the Gateway's validation, not a
  divergent set.
