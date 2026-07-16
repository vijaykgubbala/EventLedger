# Simplify Patterns

Recurring code quality issues identified by /simplify across executions.
Read this before implementing to avoid repeating past mistakes.

## Patterns

- **Console.Out-capture test boilerplate duplicates silently**: any new test that needs to inspect Serilog's JSON console output (redirect `Console.Out` to a `StringWriter`, restore in `finally`, filter/parse lines) should use the shared `ConsoleLogCapture` helper in `tests/EventLedger.Gateway.Tests/ConsoleLogCapture.cs` (`CaptureAsync`/`ParseLogLinesWithTraceId`) rather than re-inlining the pattern — it grew to 3 near-identical copies before being extracted during issue #4's `/simplify` pass. *(First seen: 2026-07-15)*
