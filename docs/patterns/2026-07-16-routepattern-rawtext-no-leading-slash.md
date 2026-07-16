---
title: RoutePattern.RawText never carries a leading slash, regardless of the attribute route
date: 2026-07-16
related: [../plans/5_observability-plan.md]
---

## Pattern

When reading the matched route's template at runtime — via
`(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText` — the
returned string never has a leading `/`, even if the controller's route
attribute was written with one (e.g. `[HttpGet("/health")]`). The value
comes back as `health`, not `/health`; a parameterized route like
`[Route("accounts")]` + `[HttpGet("{accountId}/balance")]` comes back as
`accounts/{accountId}/balance`, not `/accounts/{accountId}/balance`.

This is easy to get wrong by assumption: it's natural to expect a
"route" string to look like the URL path it matches, complete with a
leading slash. It doesn't. This was only discovered by actually running
a test and reading the captured value, not by reasoning about the API
in advance.

## Guidance

Any code that reads `RoutePattern.RawText` for logging, tagging (e.g. an
OpenTelemetry metric's `endpoint` tag), or comparison against a literal
path must account for the missing leading slash — either compare against
the no-slash form directly, or normalize (`"/" + rawText`) if a
leading-slash form is needed for consistency with logged request paths
elsewhere.

Don't assume this without checking: verify the actual string a specific
ASP.NET Core version/hosting model produces (in a test, before writing
the production assertion or tag value) rather than guessing the format
from the route attribute's own syntax.

## Examples

**Wrong** — assumes the route template includes a leading slash:

```csharp
Assert.Equal("/health", measurement.Endpoint); // fails: actual value is "health"
```

**Right** — asserts the actual, verified value:

```csharp
// RoutePattern.RawText never carries a leading slash, regardless of how the attribute
// route was written ([HttpGet("/health")] here).
Assert.Equal("health", measurement.Endpoint);
```

See `RequestMetricsMiddlewareTests.cs` (both
`tests/EventLedger.Gateway.Tests/` and
`tests/EventLedger.AccountService.Tests/`) and the route-template read in
`src/EventLedger.Gateway/Middleware/RequestMetricsMiddleware.cs` /
`src/EventLedger.AccountService/Middleware/RequestMetricsMiddleware.cs`.
