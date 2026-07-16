# Deployment & Requirements Verification Guide

This guide is for an evaluator who wants to exercise the running system
directly — deploy it, then confirm each of the assignment's requirements
actually works — rather than only reading source code or test files.
Every command below was run against a live `docker compose` deployment
before being written down; the response bodies shown are what was
actually observed, not predictions.

For deployment commands themselves (`docker compose up --build`, health
checks, teardown) and the automated-test coverage table, see
[README.md](../README.md)'s "Running the services" and "Running the
tests" sections — this guide doesn't repeat those, only builds on them.

Base URLs used throughout: `http://localhost:5099` (Event Gateway,
public-facing) and `http://localhost:5199` (Account Service, internal —
exposed here for verification only).

## Quick reference

| Endpoint | Example |
|---|---|
| `POST /events` | `curl -X POST http://localhost:5099/events -H "Content-Type: application/json" -d '{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T10:00:00Z"}'` |
| `GET /events/{id}` | `curl http://localhost:5099/events/evt-1` |
| `GET /events?account=...` | `curl "http://localhost:5099/events?account=acct-1"` |
| `GET /accounts/{id}/balance` (via Gateway) | `curl http://localhost:5099/accounts/acct-1/balance` |
| `GET /accounts/{id}/balance` (Account Service directly) | `curl http://localhost:5199/accounts/acct-1/balance` |
| `GET /health` (either service) | `curl http://localhost:5099/health` / `curl http://localhost:5199/health` |

Each row was confirmed live during this guide's authoring; for example:

```
$ curl -X POST http://localhost:5099/events \
    -H "Content-Type: application/json" \
    -d '{"eventId":"evt-cheatsheet-1","accountId":"acct-cheatsheet","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T10:00:00Z"}'

{"eventId":"evt-cheatsheet-1","accountId":"acct-cheatsheet","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T10:00:00+00:00","metadata":null,"receivedAt":"2026-07-16T15:54:43.2513198+00:00"}
# HTTP 201

$ curl http://localhost:5099/accounts/acct-cheatsheet/balance
{"accountId":"acct-cheatsheet","balance":100.0}
# HTTP 200
```

## 1. Core Functionality

The examples below build on one continuous account, `acct-verify-1`, so
the story reads start to finish rather than as disconnected snippets.

### Idempotency

Submitting the same `eventId` twice must not create a duplicate or
change the balance — the second submission returns the *original*
record.

```
$ curl -X POST http://localhost:5099/events \
    -H "Content-Type: application/json" \
    -d '{"eventId":"evt-idem-1","accountId":"acct-verify-1","type":"CREDIT","amount":200,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'

{"eventId":"evt-idem-1","accountId":"acct-verify-1","type":"CREDIT","amount":200,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00+00:00","metadata":null,"receivedAt":"2026-07-16T15:57:16.5204127+00:00"}
# HTTP 201

$ curl -X POST http://localhost:5099/events \
    -H "Content-Type: application/json" \
    -d '{"eventId":"evt-idem-1","accountId":"acct-verify-1","type":"CREDIT","amount":200,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'

{"eventId":"evt-idem-1","accountId":"acct-verify-1","type":"CREDIT","amount":200.0,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00+00:00","metadata":null,"receivedAt":"2026-07-16T15:57:16.5204127+00:00"}
# HTTP 200 — same receivedAt as the first response, not a fresh one

$ curl http://localhost:5099/accounts/acct-verify-1/balance
{"accountId":"acct-verify-1","balance":200.0}
# HTTP 200 — unchanged by the duplicate
```

### Out-of-order tolerance

Three events for the same account are submitted with `eventTimestamp`s
in a different order than they arrive (latest first, earliest second,
middle third):

```
$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-ooo-later","accountId":"acct-verify-1","type":"CREDIT","amount":50,"currency":"USD","eventTimestamp":"2026-07-16T09:30:00Z"}'
# HTTP 201

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-ooo-earliest","accountId":"acct-verify-1","type":"DEBIT","amount":30,"currency":"USD","eventTimestamp":"2026-07-16T09:10:00Z"}'
# HTTP 201

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-ooo-middle","accountId":"acct-verify-1","type":"CREDIT","amount":80,"currency":"USD","eventTimestamp":"2026-07-16T09:20:00Z"}'
# HTTP 201
```

The listing is sorted by `eventTimestamp`, not arrival order:

```
$ curl "http://localhost:5099/events?account=acct-verify-1"

[
  {"eventId":"evt-idem-1",      "eventTimestamp":"2026-07-16T09:00:00+00:00", "type":"CREDIT", "amount":200.0},
  {"eventId":"evt-ooo-earliest","eventTimestamp":"2026-07-16T09:10:00+00:00", "type":"DEBIT",  "amount":30.0},
  {"eventId":"evt-ooo-middle",  "eventTimestamp":"2026-07-16T09:20:00+00:00", "type":"CREDIT", "amount":80.0},
  {"eventId":"evt-ooo-later",   "eventTimestamp":"2026-07-16T09:30:00+00:00", "type":"CREDIT", "amount":50.0}
]
# HTTP 200 (abridged for readability — the real response includes every field)
```

And the balance is correct regardless of arrival order:
`200 + 50 − 30 + 80 = 300`

```
$ curl http://localhost:5099/accounts/acct-verify-1/balance
{"accountId":"acct-verify-1","balance":300.0}
# HTTP 200
```

### Balance computation

Net balance is `SUM(CREDIT) − SUM(DEBIT)`. The account above already
demonstrates the mixed-credit/debit case (`300.0`). A never-used account
demonstrates the zero-transaction edge case — the Account Service never
returns `404` for an unknown account, only a `0` balance:

```
$ curl http://localhost:5099/accounts/acct-verify-never-used/balance
{"accountId":"acct-verify-never-used","balance":0}
# HTTP 200
```

### Validation

Every rejection case in `EventValidator.cs`, each returning `400` with
a specific field name and message:

```
$ curl -X POST http://localhost:5099/events -d '{"accountId":"acct-verify-1","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'
{"error":"validation_error","message":"eventId is required","details":{"field":"eventId"}}
# HTTP 400 — missing eventId

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-bad-2","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'
{"error":"validation_error","message":"accountId is required","details":{"field":"accountId"}}
# HTTP 400 — missing accountId

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-bad-3","accountId":"acct-verify-1","type":"PAYMENT","amount":100,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'
{"error":"validation_error","message":"type must be exactly \"CREDIT\" or \"DEBIT\"","details":{"field":"type"}}
# HTTP 400 — invalid type

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-bad-4","accountId":"acct-verify-1","type":"CREDIT","amount":0,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'
{"error":"validation_error","message":"amount must be greater than 0","details":{"field":"amount"}}
# HTTP 400 — amount <= 0

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-bad-5","accountId":"acct-verify-1","type":"CREDIT","amount":100,"eventTimestamp":"2026-07-16T09:00:00Z"}'
{"error":"validation_error","message":"currency is required","details":{"field":"currency"}}
# HTTP 400 — missing currency

$ curl -X POST http://localhost:5099/events -d '{"eventId":"evt-bad-6","accountId":"acct-verify-1","type":"CREDIT","amount":100,"currency":"USD","eventTimestamp":"not-a-date"}'
{"error":"validation_error","message":"eventTimestamp must be a valid ISO 8601 timestamp","details":{"field":"eventTimestamp"}}
# HTTP 400 — malformed eventTimestamp
```

(All requests above include `-H "Content-Type: application/json"`,
omitted from the second command onward for brevity.)

## 2. Service Separation

This is a structural property, not runtime behavior a single `curl`
command can demonstrate. `docker-compose.yml` builds each service from
its own independent Docker context (`./src/EventLedger.Gateway`,
`./src/EventLedger.AccountService`) with no shared volume or network
dependency beyond the Compose network itself, and each service owns its
own file-based SQLite database inside its own container — there is no
shared database or in-process state between them. See
[architecture/vertical-architecture.md](../architecture/vertical-architecture.md)
for the full rationale.

## 3. Distributed Tracing

Submit an event, then grep both services' container logs for the trace
ID the Gateway generated for that request. Note: Serilog's `JsonFormatter`
puts `TraceId` at the top level of each JSON log line *and* duplicates it
inside a nested `Properties` object — either location works for a grep.

```
$ curl -s -o /dev/null -X POST http://localhost:5099/events \
    -H "Content-Type: application/json" \
    -d '{"eventId":"evt-trace-demo","accountId":"acct-trace-demo","type":"CREDIT","amount":10,"currency":"USD","eventTimestamp":"2026-07-16T09:00:00Z"}'

$ docker compose logs gateway --since 30s | grep '"Path":"/events"' | grep -o '"TraceId":"[a-f0-9]*"'
"TraceId":"fee82614b521da9c5c5edf4de46966ef"

$ docker compose logs account-service --since 30s | grep -c "fee82614b521da9c5c5edf4de46966ef"
10
```

Ten log lines in the Account Service's own output reference the exact
same trace ID the Gateway generated — the trace genuinely propagated
across the HTTP boundary via the `traceparent` header, it wasn't just
independently regenerated by each service.

## 4. Observability

**Structured logging and health checks** are already covered by
[README.md](../README.md)'s "Running the services" section
(`curl http://localhost:5099/health` / `:5199/health`, both returning
`{"status":"ok","database":"ok"}`) — see the log output above for the
JSON structure (`Timestamp`, `Level`, `MessageTemplate`, `TraceId`,
`ServiceName`, per service).

**Custom metric**: both services record a request-count `Counter` via
OpenTelemetry (`RequestMetricsMiddleware`, tagged by endpoint and status
code). This deployment deliberately registers no metrics exporter — no
Prometheus endpoint, nothing queryable over HTTP — per
[architecture/observability.md](../architecture/observability.md)'s
scope decision, so there is no `curl` command that can show it directly.
The actual proof that the metric is recorded correctly is
`tests/EventLedger.Gateway.Tests/RequestMetricsMiddlewareTests.cs` and
its Account Service equivalent, both of which subscribe a
`System.Diagnostics.Metrics.MeterListener` directly to the in-process
`Meter` and assert real recorded `(value, tags)` pairs — run via
`dotnet test` (see README's "Running the tests" section).
