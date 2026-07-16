using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.Gateway.Tests;

public class GatewayTraceparentPropagationTests
{
    [Fact]
    public async Task Request_WithInboundTraceparentHeader_ContinuesTheSameTraceIdInTheLog()
    {
        // ASP.NET Core's hosting layer already extracts an inbound W3C traceparent into
        // Activity.Current independently of whether OpenTelemetry is registered (confirmed
        // empirically: this test passed before AddOpenTelemetry() existed anywhere in this
        // codebase). Kept as a regression check for OBS-1's "extracts" requirement, not as
        // proof that OTel registration is what makes it work. The half of propagation that
        // genuinely requires OTel — outbound header injection — can't be verified with a
        // substituted HttpMessageHandler at all (see
        // GatewayToAccountServiceFullFlowTests.PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs
        // and its CreateFactoriesWithRealNetworking() for why, and where that's actually tested).
        const string knownTraceId = "0af7651916cd43dd8448eb211c80319c";
        var traceparent = $"00-{knownTraceId}-b7ad6b7169203331-01";

        var originalOut = Console.Out;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("traceparent", traceparent);

            await client.GetAsync("/health");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOutput.ToString();
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{') && line.Contains("TraceId"));

        Assert.NotNull(jsonLine);

        using var doc = JsonDocument.Parse(jsonLine);

        Assert.Equal(knownTraceId, doc.RootElement.GetProperty("TraceId").GetString());
    }
}
