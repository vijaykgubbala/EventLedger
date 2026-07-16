using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.Gateway.Tests;

public class GatewayLoggingTests
{
    [Fact]
    public async Task Request_ProducesJsonLogLineWithServiceNameAndTraceId()
    {
        var originalOut = Console.Out;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            await client.GetAsync("/");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOutput.ToString();
        // "ServiceName" is on every line (a global enricher, including
        // startup/lifecycle messages with no active request). Require
        // both properties on the same line to actually find a
        // request-processing entry, not just any log line.
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{') && line.Contains("ServiceName") && line.Contains("TraceId"));

        Assert.NotNull(jsonLine);
        Assert.Contains("\"ServiceName\":\"EventGateway\"", jsonLine);
        Assert.Matches(new Regex("\"TraceId\":\"[0-9a-f]{32}\""), jsonLine);
    }
}
