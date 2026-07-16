---
title: WebApplicationFactory<T> always forces TestServer as IServer — UseKestrel() alone is silently ignored
date: 2026-07-16
related: [2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md, ../plans/4_distributed-tracing-plan.md]
---

## Pattern

`Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TEntryPoint>`
registers `Microsoft.AspNetCore.TestHost.TestServer` as the app's `IServer`
unconditionally, as the **last** step of its own internal host-configuration
pipeline — after any `WithWebHostBuilder(...)` callback the caller supplies
has already run. Calling `.UseKestrel()` from `WithWebHostBuilder(...)` does
not override this: `UseKestrel()`'s own registration is added earlier, so
the factory's later `TestServer` registration simply wins. The practical
symptom (confirmed empirically, in this order):

1. With `builder.UseKestrel(); builder.UseUrls("http://127.0.0.1:0")` inside
   `WithWebHostBuilder(...)`: `IServerAddressesFeature.Addresses` stays at
   the literal, unresolved `"http://127.0.0.1:0"` forever — not because the
   port never got assigned, but because the registered `IServer` is
   actually `TestServer`, which never binds a real socket and never rewrites
   `:0` to a real port. Any outbound call to that literal address fails
   with `SocketException (10049): The requested address is not valid in
   its context.`
2. Switching to a **fixed** port and adding `services.RemoveAll<IServer>()`
   inside the same `WithWebHostBuilder(...)` callback, before `UseKestrel()`,
   still doesn't work — `GetRequiredService<IServer>()` still resolves to
   `TestServer`. This confirms the ordering problem: WAF's own `TestServer`
   registration happens strictly after `WithWebHostBuilder(...)`, not before,
   so removing it too early just means WAF re-adds it afterward.
3. The registration only actually flips to Kestrel when the removal +
   `UseKestrel()` + `UseUrls(...)` happen inside an **overridden
   `CreateHost(IHostBuilder builder)`** — this is the actual last
   configuration hook WAF calls before `builder.Build()`, so nothing runs
   afterward to re-inject `TestServer`.
4. Once `IServer` is genuinely `KestrelServerImpl`, a **new** failure
   appears: `WebApplicationFactory<TEntryPoint>.EnsureServer()` — the
   internal method backing `.Server`, `.Services`, and `.CreateClient()` —
   unconditionally hard-casts the resolved `IServer` to `TServer`
   (`TestServer`, for the plain one-generic-argument factory used here),
   and throws `InvalidCastException: Unable to cast object of type
   'Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl' to type
   'Microsoft.AspNetCore.TestHost.TestServer'`. This happens **after**
   `CreateHost` has already completed and the real host has already been
   built and started — the cast is the very next line, purely for
   populating the factory's own `.Server`/`.Client` properties.

Net effect: `WebApplicationFactory<TEntryPoint>` (the plain, one-type-arg
form) cannot be made to genuinely expose a working `.Server`/`.Services`/
`.CreateClient()` once it's backed by real Kestrel — every public accessor
that would let you use the factory normally throws. The host itself is
real and listening; the factory's own convenience surface is not usable
with it.

This was discovered while fixing `workflow-review 4`'s F1 finding for
`GatewayToAccountServiceFullFlowTests.cs` — adding a response-status
assertion to `PostEvents_TraceparentPropagatesOverRealNetworkCall_...`
turned a silently-passing test into a deterministically-failing one,
surfacing that the "real networking" helper built during
`workflow-execute 4` had never actually worked (see
[4_distributed-tracing-plan.md](../plans/4_distributed-tracing-plan.md),
Decisions Made). The `Assert.Single(traceIds)` assertion it originally
had passed trivially because the Gateway's own single request-processing
`Activity` produced one `TraceId` regardless of whether the Account
Service was ever reached.

## Guidance

To get a `WebApplicationFactory`-hosted ASP.NET Core app genuinely
listening on a real socket for a test:

1. Subclass `WebApplicationFactory<TProgram>` and override
   `CreateHost(IHostBuilder builder)`. Do the `IServer` removal and
   `UseKestrel()`/`UseUrls(...)` configuration **inside this override**,
   via `builder.ConfigureWebHost(...)` — not via the outer
   `WithWebHostBuilder(...)` call, which runs too early.
2. Use a **fixed** port, not `:0`. Dynamic-port discovery via
   `IServerAddressesFeature.Addresses` does not reliably resolve in this
   hosting configuration (see point 1 above) — a fixed high port removes
   the discovery step entirely. A single test class using
   `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
   (already required in both test projects for `Console.Out`/`Log.Logger`
   reasons) means no other test in the same run can contend for the port.
3. Call `builder.Build()` then `host.Start()` explicitly inside the
   override, and stash the returned `IHost` on your subclass (a plain
   property) before returning it. This is the only reliable way to reach
   the real host's `IServiceProvider` afterward.
4. Never call `.Server`, `.Services`, or `.CreateClient()`/
   `.CreateDefaultClient()` on the factory instance again once it's
   Kestrel-backed — they all route through `EnsureServer()`'s broken cast.
   Triggering that cast once (e.g. via a first, throwaway `.Services`
   access to force `CreateHost` to run) is fine — catch and ignore the
   specific `InvalidCastException` it throws, since your own stashed
   `IHost` reference is already valid and started by that point. Catch
   only `InvalidCastException`, not a bare `catch` — a genuine startup
   failure (e.g. the fixed port already in use) should still propagate.

## Examples

**Wrong** — `UseKestrel()` from `WithWebHostBuilder` is silently ignored,
`TestServer` still wins:

```csharp
var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
{
    builder.ConfigureServices(services => services.RemoveAll<IServer>());
    builder.UseKestrel();
    builder.UseUrls("http://127.0.0.1:58734");
});

// Still TestServer — WAF's own registration runs after this callback.
var actual = factory.Services.GetRequiredService<IServer>();
```

**Right** — configure inside `CreateHost`, keep your own host reference,
and expect (and ignore) the one `InvalidCastException`:

```csharp
private sealed class RealKestrelWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    public IHost? RealHost { get; private set; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureWebHost(webHostBuilder =>
        {
            webHostBuilder.ConfigureServices(services => services.RemoveAll<IServer>());
            webHostBuilder.UseKestrel();
            webHostBuilder.UseUrls("http://127.0.0.1:58734");
        });

        var host = builder.Build();
        host.Start();
        RealHost = host;
        return host;
    }
}

var factory = new RealKestrelWebApplicationFactory<AccountServiceProgram>()
    .WithWebHostBuilder(builder => builder.ConfigureServices(ConfigureAccountServiceDb));

try
{
    _ = factory.Services; // forces CreateHost to run; RealHost is now populated
}
catch (InvalidCastException)
{
    // Expected — WAF's own (TServer) cast against the now-real Kestrel IServer.
}
```

See `RealKestrelWebApplicationFactory<TProgram>` and
`CreateFactoriesWithRealNetworking()` in
`tests/EventLedger.Gateway.Tests/GatewayToAccountServiceFullFlowTests.cs`
for the applied fix.
