using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace EventLedger.AccountService.Tests;

public class OpenTelemetryRegistrationTests
{
    [Fact]
    public async Task AccountServiceHost_RegistersTracerProvider()
    {
        // See the Gateway-side twin of this test for why: TracerProvider resolvability is the
        // one thing that directly depends on AddOpenTelemetry() having run, unlike traceparent
        // propagation itself, which the BCL already provides unconditionally.
        await using var factory = new WebApplicationFactory<Program>();

        var tracerProvider = factory.Services.GetService<TracerProvider>();

        Assert.NotNull(tracerProvider);
    }
}
