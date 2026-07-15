# Logging (.NET / Serilog)

Concrete Serilog setup for both services. Rationale for why Serilog + JSON
console (and not an external log vendor) lives in
[../architecture/observability.md](../architecture/observability.md); this
document is the "how," specific to .NET.

## Required configuration

Both services configure Serilog identically (shared setup, service-specific
`ServiceName` property):

```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "EventGateway") // or "AccountService"
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

builder.Host.UseSerilog();
```

`Enrich.FromLogContext()` is **required** — see
[observability.md](../architecture/observability.md#structured-logging) for
why. Without it, the trace ID has to be passed explicitly to every log
call, and it will inevitably be missed on some code path.

## Required fields on every log entry

The `JsonFormatter` console sink produces these automatically, plus
whatever's been pushed into `LogContext`:

| Field | Source |
|---|---|
| `@t` (timestamp) | Automatic (Serilog `JsonFormatter`) |
| `@l` (level) | Automatic |
| `@m` / `@mt` (message) | Automatic, from the log call |
| `ServiceName` | `Enrich.WithProperty`, set once at startup per service |
| `TraceId` | Pushed into `LogContext` by request middleware (see below), sourced from the current `Activity`/OpenTelemetry span |

## Pushing the trace ID into `LogContext`

A single piece of middleware, added once per service, pushes the current
trace ID into `LogContext` at the start of every request:

```csharp
app.Use(async (context, next) =>
{
    var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
    using (LogContext.PushProperty("TraceId", traceId))
    {
        await next();
    }
});
```

Because `Activity.Current` is populated by the ASP.NET Core OpenTelemetry
instrumentation before this middleware runs (instrumentation is registered
early in the pipeline), this requires no manual trace-header parsing — see
[observability.md](../architecture/observability.md#distributed-tracing).
Every `Log.*` call anywhere in that request's call stack — controller,
application layer, EF Core query logging, the outbound Account Service
call — automatically includes `TraceId` in its JSON output as long as it
executes within this `using` scope.

## What to log

- **Request start/end**, at `Information` level, including method, path,
  and resulting status code — ASP.NET Core's built-in request logging
  (or a thin custom middleware) covers this; don't hand-log every
  controller action individually.
- **Validation failures**, at `Information` or `Warning` level (a `400` is
  an expected outcome, not a fault — don't log it as an error).
- **Account Service call failures** (timeout, circuit open, error
  response), at `Warning` level on the Gateway — expected under degraded
  conditions, not a bug.
- **Unhandled exceptions**, at `Error` level, via a single global exception
  handler/middleware — not scattered `try`/`catch`/`Log.Error` blocks
  around individual operations.

## Anti-patterns to avoid

- **Do not configure Serilog without `Enrich.FromLogContext()`.** Trace
  correlation silently breaks without it.
- **Do not manually pass a trace ID string as a parameter into individual
  `Log.*` calls.** Push it into `LogContext` once per request and let
  enrichment handle the rest.
- **Do not log a `400` validation failure at `Error` level.** Reserve
  `Error` for genuinely unexpected faults; a client sending an invalid
  payload is expected behavior the system correctly rejected.
- **Do not add a second logging framework or a raw `Console.WriteLine`
  anywhere.** Serilog, via the shared configuration above, is the only
  logging path in either service.
- **Do not configure a non-JSON console sink "for readability" during
  development.** JSON-to-console is the standing configuration in every
  environment this system runs in — see
  [observability.md](../architecture/observability.md).
