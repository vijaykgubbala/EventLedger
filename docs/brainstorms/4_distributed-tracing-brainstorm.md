# Brainstorm: Distributed tracing — OpenTelemetry, traceparent propagation

**Date:** 2026-07-15
**Issue:** #4

## Problem Statement

Currently, a request that flows through the Gateway and then the Account Service produces two independent log streams with no way to correlate them — a developer debugging a failed request has to guess which Account Service log lines correspond to which Gateway request. This story wires up the OpenTelemetry SDK on both services (ASP.NET Core instrumentation + `HttpClient` instrumentation) so that a W3C `traceparent` is generated at the Gateway, automatically propagated to the Account Service on the one outbound HTTP call, and both services' existing Serilog JSON logs carry a matching `TraceId` for the whole request — with zero hand-rolled header code.

**Scope boundary confirmed via the milestone**: "Phase 2 - Observability" also contains issue #5 ("Story 4: Observability — structured logging, health checks, custom metric"), which is where the custom `Counter` metric and the `/health` DB-connectivity upgrade belong. This story is tracing only.

## Codebase Context

- **`architecture/observability.md`** already fully specifies the target design — this story is implementation of a settled decision, not new design:
  - "**OpenTelemetry SDK**, with ASP.NET Core and `HttpClient` instrumentation enabled on both services, using the standard **W3C `traceparent`** header for propagation."
  - "The Gateway's outbound call to the Account Service... automatically carries the `traceparent` header because `HttpClient` instrumentation injects it. **This is automatic; no service writes or reads trace headers by hand.**"
  - "Both services export/print trace and span IDs into their structured logs... sufficient for a local, solo-run system... without a separate trace backend."
  - Anti-pattern: "**Do not hand-roll trace header propagation.**"
- **`standards/logging-dotnet.md`** documents the exact `TraceLoggingMiddleware` snippet already implemented verbatim in both services, which reads `Activity.Current?.TraceId` and pushes it into Serilog's `LogContext`. The doc's own comment already anticipates this story: "Because `Activity.Current` is populated by the ASP.NET Core OpenTelemetry instrumentation before this middleware runs..." — i.e., the middleware was written in issue #3 *expecting* this story to change what populates `Activity.Current`.
- **Confirmed via issue #3's plan** (`docs/plans/3_service-separation-plan.md`, Decision 3 and Known Constraints): OpenTelemetry registration was explicitly and deliberately deferred to this story, so it could be added "in one piece rather than this story adding a partial registration #4 has to modify."
- **No code changes needed to `TraceLoggingMiddleware.cs`** in either service — it already reads `Activity.Current?.TraceId` correctly. Today that `Activity` is populated by ASP.NET Core's own built-in diagnostics (not `traceparent`-aware); after this story it's populated by OpenTelemetry's `AddAspNetCoreInstrumentation()`, which *is* `traceparent`-aware on the way in and injects it on the way out via `AddHttpClientInstrumentation()`.
- **The one cross-service call this story needs to propagate across**: `SubmitEventHandler.cs`'s `httpClientFactory.CreateClient("AccountService")` call, registered as a plain named client in `AddGatewayInfrastructure()` (`src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs`). The Account Service makes zero outbound calls, so it only needs the inbound (`AddAspNetCoreInstrumentation()`) side.
- **Confirmed no OpenTelemetry NuGet packages exist yet** in either `.csproj` — both currently reference only `Microsoft.EntityFrameworkCore.Sqlite` and `Serilog.AspNetCore`.

## Q&A Decisions

**Q1: Where should the OpenTelemetry SDK registration live in each service's `ServiceCollectionExtensions.cs`?**
A: Extend the existing `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` methods in place — matches the pattern already used for DbContext registration in issue #2, and is exactly what issue #3's own brainstorm anticipated ("adds the full `AddOpenTelemetry()` registration in one piece").

**Q2: Should the `TracerProvider` register a span exporter (e.g. Console), or none at all?**
A: None. The story's actual requirement is a matching `TraceId` in both services' existing Serilog JSON logs, which only needs `Activity.Current` populated correctly — `AddAspNetCoreInstrumentation()`/`AddHttpClientInstrumentation()` do that regardless of whether any exporter is registered. No exporter keeps registration minimal and avoids a second, separate stream of span/duration output nothing in OBS-1/OBS-2 asks for.

**Q3: How should OBS-2 ("the same trace ID appears in both services' logs for one request, end to end") be verified?**
A: An automated test, reusing two patterns already in the codebase: `GatewayToAccountServiceFullFlowTests.cs` (issue #2's real two-in-process-host wiring via `TestServer.CreateHandler()`) and the `Console.Out`-redirection technique issue #3's `GatewayLoggingTests.cs`/`AccountServiceLoggingTests.cs` already use to capture and assert on JSON log output. One real `POST /events` driven through both services, both captured log streams parsed, and asserted to contain the same `TraceId`.

**Q4: Should OpenTelemetry's resource `service.name` reuse the exact string already passed to `BootstrapLogging(serviceName)`?**
A: Yes — reuse the same value ("EventGateway"/"AccountService") so Serilog's `ServiceName` log property and OTel's resource attribute never drift apart across two separately-configured systems referring to the same service.

## Proposed Approaches

### Approach 1: Full OpenTelemetry SDK registration, no exporter, shared service-name, automated cross-service log test (Recommended)

Add `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Instrumentation.AspNetCore`/`OpenTelemetry.Instrumentation.Http` to both `.csproj`s. In each `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()`, add:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()); // Gateway only needs this for its outbound call; harmless no-op to include on the Account Service too for consistency, since it has no outbound calls to instrument
```

`serviceName` is the same string already passed to `BootstrapLogging(serviceName)`, threaded into `AddGatewayInfrastructure`/`AddAccountServiceInfrastructure` as a parameter (or read from the same constant) so both systems agree.

**Pros:**
- Matches the architecture doc exactly — no design deviation to justify later.
- Zero changes to `TraceLoggingMiddleware.cs`, `Program.cs`, or any Application-layer code — this is purely additive infrastructure registration.
- One call site per service, consistent with every other story's DI-registration pattern in this codebase.
- The automated test directly proves OBS-2 rather than relying on a human eyeballing console output once.

**Cons:**
- Requires two new NuGet packages per service (small, but non-zero addition to the dependency surface).
- The cross-service log-matching test is the most involved piece of new test infrastructure in this story (though it reuses two already-proven techniques rather than inventing a third).

### Approach 2: Bare `System.Diagnostics.Activity`/`DistributedContextPropagator`, no OpenTelemetry SDK packages

.NET 6+'s `HttpClient` already propagates W3C `traceparent` automatically via `System.Net.Http.DiagnosticsHandler` and `DistributedContextPropagator.Current`, and ASP.NET Core's Kestrel hosting already creates an `Activity` per request that reads any inbound `traceparent` and continues it — this is *why* `TraceLoggingMiddleware` already "works" today, pre-OTel, per a finding already recorded in `docs/reviews/3_service-separation.json`. In principle, basic trace-ID propagation could work with zero new NuGet packages at all.

**Pros:**
- Zero new dependencies.
- Slightly less registration code.

**Cons:**
- Contradicts `architecture/observability.md`'s explicit, already-settled decision ("OpenTelemetry SDK... enabled on both services") — would require an architecture-doc edit to justify, not just a story-level choice.
- Doesn't establish the OpenTelemetry `Meter`/resource model that issue #5's custom metric will need — this story would end up half-building infrastructure #5 has to redo anyway, the exact problem issue #3's brainstorm avoided by deferring OTel registration wholesale to this story.
- No real `service.name` resource concept without the SDK's `ConfigureResource`, weakening Q4's decision to unify Serilog/OTel service naming.

### Approach 3: Hand-rolled `traceparent` header parsing/forwarding

Manually read the inbound `traceparent` header in Gateway middleware, manually attach it as an outbound header on the `HttpClient` call to the Account Service, manually parse it back out in Account Service middleware.

**Pros:**
- None found that outweigh the cons — every benefit here is also available in Approach 1 for less risk.

**Cons:**
- Explicitly forbidden: `architecture/observability.md`'s anti-patterns section states this directly — "manual `traceparent` header code is redundant with what the instrumentation already does correctly, and is a common source of subtly broken propagation (wrong header casing, missing on retry, etc.)."
- Not seriously considered; included for completeness.

## Recommendation

**Approach 1.** It's the only approach that matches the already-settled architecture decision without requiring a doc edit, requires no changes to already-correct code (`TraceLoggingMiddleware.cs`), fits this codebase's established "one infrastructure call site per story" pattern (Q1), and its automated verification (Q3) gives OBS-2 a real regression test rather than a one-time manual check. Given the issue's own effort estimate ("~0.25h — mostly NuGet + `Program.cs` registration"), this is also simply the fastest path: two package references and a handful of lines per service, all additive.

## Related Docs

- [architecture/observability.md](../../architecture/observability.md) — the settled design this story implements
- [standards/logging-dotnet.md](../../standards/logging-dotnet.md) — the `TraceLoggingMiddleware` pattern this story's registration feeds
- [docs/plans/3_service-separation-plan.md](../plans/3_service-separation-plan.md) — Decision 3 and Known Constraints, recording why OTel was deferred to this story
- [docs/reviews/3_service-separation.json](../reviews/3_service-separation.json) — the empirical finding that `Activity.Current`/`TraceId` logging already works pre-OTel, relevant background for why `TraceLoggingMiddleware` needs no changes
