using System.Text.Json;

namespace EventLedger.AccountService.Tests;

// Mirrors tests/EventLedger.Gateway.Tests/ConsoleLogCapture.cs — a deliberate per-test-project
// copy, not leftover duplication. Same rationale as SqliteTempDbFixture existing separately in
// both test projects: it's internal to each project, and test projects don't reference each
// other in this codebase's layout (standards/backend-architecture.md's "one test project per
// service"), so there's no way to share it without introducing a cross-project reference.
internal static class ConsoleLogCapture
{
    public static async Task<string> CaptureAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);

        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capturedOutput.ToString();
    }

    public static IReadOnlyList<JsonElement> ParseLogLinesWithTraceId(string capturedOutput) =>
        capturedOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{') && line.Contains("TraceId"))
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.Clone();
            })
            .ToList();
}
