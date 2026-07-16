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
