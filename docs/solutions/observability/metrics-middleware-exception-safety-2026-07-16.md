---
title: "RequestMetricsMiddleware silently dropped its counter on unhandled exceptions"
date: 2026-07-16
category: observability
related: [../../patterns/2026-07-15-cancellation-token-propagation.md, ../../plans/5_observability-plan.md, ../../reviews/5_observability.json]
---

# RequestMetricsMiddleware silently dropped its counter on unhandled exceptions

## Symptoms

The new request-count `Counter<long>` metric (issue #5, OBS-5) worked
correctly for every request that completed normally — but a
`workflow-review` pass flagged that it had never been proven to work for
a request that *failed*. Nothing crashed and no test failed; the gap was
only visible by reasoning about the code path, then confirmed with a
test built specifically to force an unhandled exception.

## Root Cause

`RequestMetricsMiddleware` recorded the counter in a single line placed
immediately after `await next()`:

```csharp
return app.Use(async (context, next) =>
{
    await next();

    var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
    RequestCounter.Add(1, ...);
});
```

If `next()` throws — and it can, since neither service registers a
global exception-handling middleware — the exception propagates straight
through this `app.Use(...)` lambda, skipping the `RequestCounter.Add(...)`
call entirely. The metric's whole purpose, per
`architecture/observability.md`, is knowing whether requests are
succeeding; silently dropping exactly the failure case it exists to
surface defeats that purpose.

## Solution

Wrap `await next()` in `try`/`catch`. The success path records the
real final `context.Response.StatusCode`. The catch block explicitly
records `StatusCodes.Status500InternalServerError` — `Response.StatusCode`
is not reliably set to its final value yet at this point in the
pipeline (that assignment happens in ASP.NET Core's hosting layer, in an
outer catch above this middleware) — then rethrows, so normal exception
propagation and logging are unaffected:

```csharp
return app.Use(async (context, next) =>
{
    try
    {
        await next();
        RecordMeasurement(context, context.Response.StatusCode);
    }
    catch
    {
        RecordMeasurement(context, StatusCodes.Status500InternalServerError);
        throw;
    }
});
```

Verified with a new test (`RequestThatThrowsUnhandledException_StillRecordsMeasurement`,
both services) that forces a *genuine* unhandled exception — not a mock —
by dropping the underlying SQLite table (`DROP TABLE events` /
`DROP TABLE transactions`) on an isolated temp database after host
startup, then issuing a request that queries it. Confirmed genuinely red
before the fix (measurement collection empty despite a real `500`
response) and green after.

## Key Insight

Any middleware that instruments a request "after `await next()`" only
observes the success path unless it explicitly wraps the call in
`try`/`catch` (or `try`/`finally`) — and the failure path is often the
one the instrumentation exists to catch.

## Prevention

When writing any middleware that measures, logs, or reports on request
outcome, treat "the wrapped call throws" as the default case to design
for, not an edge case to bolt on afterward — write the exception-path
test *before* the middleware's happy-path test, the same way a
resiliency/circuit-breaker test would be written first.

## Related Docs

- [docs/plans/5_observability-plan.md](../../plans/5_observability-plan.md) — OBS-5 implementation plan.
- [docs/reviews/5_observability.json](../../reviews/5_observability.json) — findings F1/F2, the review pass that caught this.
- [docs/handoffs/2026-07-16-072446-5_observability-handoff.md](../../handoffs/2026-07-16-072446-5_observability-handoff.md) — issue #5 handoff.
