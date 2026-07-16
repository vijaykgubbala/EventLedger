using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.AccountService.Tests;

public class AccountServiceLoggingTests
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

            await client.GetAsync("/health");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = capturedOutput.ToString();
        // "ServiceName" is on every line (a global enricher, including
        // startup/lifecycle messages with no active request), so it can't
        // narrow the search — only "TraceId" distinguishes a
        // request-processing line from a pre-request lifecycle line.
        var jsonLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{') && line.Contains("TraceId"));

        Assert.NotNull(jsonLine);

        using var doc = JsonDocument.Parse(jsonLine);
        var properties = doc.RootElement.GetProperty("Properties");

        Assert.Equal("AccountService", properties.GetProperty("ServiceName").GetString());
        Assert.Matches("^[0-9a-f]{32}$", doc.RootElement.GetProperty("TraceId").GetString());
    }
}
