using Microsoft.AspNetCore.Mvc.Testing;

namespace EventLedger.AccountService.Tests;

public class AccountServiceLoggingTests
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

        Assert.Equal("AccountService", properties.GetProperty("ServiceName").GetString());
        Assert.Matches("^[0-9a-f]{32}$", line.GetProperty("TraceId").GetString());
    }

    // OBS-3 acceptance test: every log line must be JSON with timestamp, level, service name,
    // trace ID, and message. ServiceName/TraceId are already covered above; this asserts the
    // remaining three fields explicitly in one place, closing the gap between "it happens to
    // work" (all five have existed since issues #3/#4) and "it's verified."
    [Fact]
    public async Task Request_ProducesJsonLogLineWithTimestampLevelAndMessage()
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

        Assert.True(DateTimeOffset.TryParse(line.GetProperty("Timestamp").GetString(), out _));
        Assert.Equal("Information", line.GetProperty("Level").GetString());
        Assert.False(string.IsNullOrWhiteSpace(line.GetProperty("MessageTemplate").GetString()));
    }
}
