using System.Text.Json;

namespace EventLedger.Gateway.Tests;

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
                // Clone so the element stays valid after the document (and its pooled backing
                // buffer) is disposed — callers use the returned elements after this returns.
                return doc.RootElement.Clone();
            })
            .ToList();
}
