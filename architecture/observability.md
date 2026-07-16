# Observability

Event Ledger's observability stack is intentionally minimal and
library-based — no external observability vendor, no collector
infrastructure to run. It covers the three things the assignment requires:
distributed tracing, structured logging, and at least one custom metric.

## Distributed tracing

**OpenTelemetry SDK**, with ASP.NET Core and `HttpClient` instrumentation
enabled on both services, using the standard **W3C `traceparent`** header
for propagation.

- Each incoming request to the Gateway starts a trace (ASP.NET Core
  instrumentation creates the root span automatically).
- The Gateway's outbound call to the Account Service — made via the typed
  `HttpClient` described in [resiliency.md](resiliency.md) — automatically
  carries the `traceparent` header because `HttpClient` instrumentation
  injects it. **This is automatic; no service writes or reads trace headers
  by hand.**
- The Account Service's ASP.NET Core instrumentation picks up the incoming
  `traceparent` and continues the same trace, so a single client request
  produces one connected trace spanning both services.
- Both services export/print trace and span IDs into their structured logs
  (see below) so a trace can be followed through log output without a
  separate trace backend — sufficient for a local, solo-run system where
  standing up a full backend (Jaeger/Zipkin) is a bonus, not a requirement.

## Structured logging

**Serilog**, JSON console sink, on both services. Every log entry is a JSON
object containing at minimum: timestamp, log level, service name,
message, and — via `Enrich.FromLogContext()` — the current trace ID and
span ID for that request.

`Enrich.FromLogContext()` is **required**, not optional: without it, trace
IDs have to be threaded manually into every log call, which is exactly the
kind of thing that gets forgotten on one code path and silently breaks
traceability. With it, a middleware pushes the current trace ID into the
`LogContext` once per request and every log statement in that request's
call stack picks it up automatically.

Concrete field list and setup are in
[standards/logging-dotnet.md](../standards/logging-dotnet.md). There is no
external log vendor (no Seq, no ELK, no cloud logging sink) — JSON to
console is sufficient for a local system, and JSON keeps the output
machine-parseable if someone wants to pipe it into `jq` or a local log
viewer.

## Health checks

`GET /health` on both services returns service status and a basic
diagnostic: database connectivity (a trivial query against the local
SQLite file).

> **Sequencing note:** issue #3 (Service separation) shipped `/health` as a
> trivial `{"status": "ok"}` placeholder, with no DB-connectivity check —
> deliberate and temporary, since no `DbContext` existed until issue #2.
> Issue #5 (Observability) upgraded the same route in place to match the
> full contract below — both services now return
> `{"status": "ok"|"degraded", "database": "ok"|"unreachable"}`, always
> `200`. See `docs/plans/5_observability-plan.md` for the implementation.

The Gateway's `/health` does **not** block on Account Service
reachability — a health check must answer quickly from local state, not
depend on a downstream service that might itself be degraded. (Account
Service reachability from the Gateway's perspective is instead surfaced by
the circuit breaker's state and by `POST /events`/balance-read error
responses — see [resiliency.md](resiliency.md).)

## Custom metric

Each service tracks **request count by endpoint and status code** as an
OpenTelemetry `Counter` metric, incremented in middleware on every request.
This satisfies the assignment's "at least one custom metric" requirement
with something genuinely useful for a solo operator: it's the simplest
signal for "is anything calling this, and is it succeeding" without needing
a metrics backend to visualize — the counter values can be logged
periodically or inspected via the OpenTelemetry SDK's console/logging
exporter. Latency histograms and error-rate-specific metrics are left as
bonus scope (see [README.md](../README.md#assumptions--bonus-scope)) rather
than required here, since one metric satisfies the requirement and adding
more only pays off if there's a backend to consume them.

## Anti-patterns to avoid

- **Do not hand-roll trace header propagation.** Use the ASP.NET Core and
  `HttpClient` OpenTelemetry instrumentation packages — manual
  `traceparent` header code is redundant with what the instrumentation
  already does correctly, and is a common source of subtly broken
  propagation (wrong header casing, missing on retry, etc.).
- **Do not log without `Enrich.FromLogContext()` configured.** Logging
  trace IDs by manually passing them as a parameter to every `Log.*` call
  is guaranteed to be inconsistent across the codebase.
- **Do not make `GET /health` call the other service or otherwise block on
  a network round-trip.** It must answer from local state only.
- **Do not introduce a metrics backend (Prometheus server, Grafana,
  cloud metrics) as a requirement.** A metric exposed via the OpenTelemetry
  SDK's own exporter is sufficient; standing up visualization
  infrastructure is bonus scope at best for this system's scale.
- **Do not add a log vendor or shipping pipeline.** JSON-to-console is the
  full extent of the logging sink for this system.
