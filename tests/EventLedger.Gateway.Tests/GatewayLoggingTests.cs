using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.Gateway.Tests;

public class GatewayLoggingTests
{
    [Fact]
    public async Task Request_ProducesJsonLogLineWithServiceNameAndTraceId()
    {
        var output = await ConsoleLogCapture.CaptureAsync(async () =>
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            await client.GetAsync("/health");
        });

        var lines = ConsoleLogCapture.ParseLogLinesWithTraceId(output);
        Assert.NotEmpty(lines);

        var line = lines[0];
        var properties = line.GetProperty("Properties");

        Assert.Equal("EventGateway", properties.GetProperty("ServiceName").GetString());
        Assert.Matches("^[0-9a-f]{32}$", line.GetProperty("TraceId").GetString());
    }
}
