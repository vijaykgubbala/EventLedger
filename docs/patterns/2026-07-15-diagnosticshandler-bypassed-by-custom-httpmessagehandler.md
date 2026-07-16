---
title: A custom primary HttpMessageHandler (stubs, TestServer.CreateHandler()) bypasses System.Net.Http.DiagnosticsHandler
date: 2026-07-15
related: [../../architecture/observability.md, ../plans/4_distributed-tracing-plan.md]
---

## Pattern

`System.Net.Http.DiagnosticsHandler` is the component that actually reads
`Activity.Current` and injects the W3C `traceparent` header onto an
outbound `HttpRequestMessage`. It is not a separately composable
`DelegatingHandler` registered via `IHttpClientFactory`'s builder
pipeline — it lives **inside `SocketsHttpHandler`'s own send
implementation**. Whenever a test (or any code) substitutes a custom
`HttpMessageHandler` as the primary handler for an `HttpClient` —
`.ConfigurePrimaryHttpMessageHandler(() => someStub)`,
`TestServer.CreateHandler()`, a hand-rolled fake `IHttpClientFactory` — it
replaces `SocketsHttpHandler` entirely, and `DiagnosticsHandler` along
with it. No `traceparent` header gets injected, **regardless of whether
OpenTelemetry's `AddHttpClientInstrumentation()` is registered** — OTel's
HTTP instrumentation works by listening to the `DiagnosticListener` events
`DiagnosticsHandler` emits; if `DiagnosticsHandler` never runs, there is
nothing for OTel to listen to, and no fallback path.

This was discovered while building issue #4's cross-service trace
propagation test. `Activity.Current` was empirically confirmed to be
correctly populated (`Recorded=true`, valid W3C `TraceId`, right
`ActivitySource`) at the exact moment of the outbound call — the context
genuinely existed — yet the captured `HttpRequestMessage` never carried a
`traceparent` header, because the test used a custom capturing
`HttpMessageHandler` as the primary handler. The same root cause then
showed up in this codebase's existing dual-host integration test
(`GatewayToAccountServiceFullFlowTests.cs`), which uses
`TestServer.CreateHandler()`: driving a request through it produced two
independently-generated trace IDs (one per service) instead of one
propagated one, confirmed via a throwaway diagnostic test before this
was understood.

## Guidance

If a test needs to verify that an HTTP header gets **injected by
framework/BCL machinery** (not application code) — `traceparent`,
`baggage`, or anything else `DiagnosticsHandler`/`DistributedContextPropagator`
is responsible for — a custom primary-handler substitution cannot observe
it, no matter how the DI container is configured. The only reliable way to
test this is to make a **genuine network call** through the real, default
`SocketsHttpHandler`: start the receiving side as a real, listening
Kestrel server (`builder.UseKestrel()` + `builder.UseUrls("http://127.0.0.1:0")`
for an OS-assigned free port, then discover the real bound address via
`IServer`/`IServerAddressesFeature` — accessing `WebApplicationFactory<T>.Services`
forces the host, including Kestrel, to actually start listening), and
point the calling `HttpClient` at that real address **without**
overriding its primary handler.

This is a different concern from "should this test avoid a real network
call for speed/isolation" — most of this codebase's tests correctly avoid
real sockets for unrelated reasons (stubbing the Account Service's
response in Gateway unit tests, `TestServer.CreateHandler()` for
DB-persistence-focused dual-host tests that don't care about header
injection). The distinction is specifically: **is what's being tested
implemented by a framework component that itself lives inside the
transport layer?** If so, only a real transport call can observe it.

## Examples

**Wrong** — cannot observe DiagnosticsHandler's header injection:

```csharp
services.AddHttpClient("AccountService")
    .ConfigurePrimaryHttpMessageHandler(() => new CapturingHandler());
// CapturingHandler.SendAsync never sees a traceparent header, even with
// AddHttpClientInstrumentation() registered — SocketsHttpHandler, and
// DiagnosticsHandler inside it, never runs.
```

**Right** — a real Kestrel listener preserves the real transport:

```csharp
var accountServiceFactory = new WebApplicationFactory<AccountServiceProgram>()
    .WithWebHostBuilder(builder =>
    {
        builder.UseKestrel();
        builder.UseUrls("http://127.0.0.1:0");
    });

var server = accountServiceFactory.Services.GetRequiredService<IServer>();
var realAddress = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();

// Gateway's HttpClient keeps its default SocketsHttpHandler — only the
// BaseAddress changes, not the primary handler.
services.AddHttpClient("AccountService", client => client.BaseAddress = new Uri(realAddress));
```

See `CreateFactoriesWithRealNetworking()` and
`PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs`
in `tests/EventLedger.Gateway.Tests/GatewayToAccountServiceFullFlowTests.cs`
for the applied fix, and
[docs/plans/4_distributed-tracing-plan.md](../plans/4_distributed-tracing-plan.md)
(Decisions Made, item 7) for the full investigation.
