using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace EventLedger.Gateway.Tests;

public class OpenTelemetryRegistrationTests
{
    [Fact]
    public async Task GatewayHost_RegistersTracerProvider()
    {
        // Unlike the propagation tests, this depends directly on AddOpenTelemetry() having
        // run — System.Net.Http.DiagnosticsHandler injects/extracts traceparent unconditionally
        // regardless of OTel registration (see
        // docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md),
        // so those tests alone don't catch an accidental deletion of the registration call.
        // TracerProvider is only resolvable from DI if AddOpenTelemetry().WithTracing(...) ran.
        await using var factory = new WebApplicationFactory<Program>();

        var tracerProvider = factory.Services.GetService<TracerProvider>();

        Assert.NotNull(tracerProvider);
    }
}
