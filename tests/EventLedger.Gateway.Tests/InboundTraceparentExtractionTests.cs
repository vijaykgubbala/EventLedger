using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.Gateway.Tests;

public class InboundTraceparentExtractionTests
{
    [Fact]
    public async Task Request_WithInboundTraceparentHeader_ContinuesTheSameTraceIdInTheLog()
    {
        // ASP.NET Core's hosting layer already extracts an inbound W3C traceparent into
        // Activity.Current independently of whether OpenTelemetry is registered (confirmed
        // empirically: this test passed before AddOpenTelemetry() existed anywhere in this
        // codebase). Kept as a regression check for OBS-1's "extracts" requirement. The half
        // of propagation that genuinely requires OTel — outbound header injection — is
        // verified in GatewayToAccountServiceFullFlowTests, since it can't be observed with a
        // substituted HttpMessageHandler at all; see
        // docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md.
        const string knownTraceId = "0af7651916cd43dd8448eb211c80319c";
        var traceparent = $"00-{knownTraceId}-b7ad6b7169203331-01";

        var output = await ConsoleLogCapture.CaptureAsync(async () =>
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("traceparent", traceparent);

            await client.GetAsync("/health");
        });

        var lines = ConsoleLogCapture.ParseLogLinesWithTraceId(output);
        Assert.NotEmpty(lines);

        Assert.Equal(knownTraceId, lines[0].GetProperty("TraceId").GetString());
    }
}
