# Simplify Patterns

Recurring code quality issues identified by /simplify across executions.
Read this before implementing to avoid repeating past mistakes.

## Patterns

- **Console.Out-capture test boilerplate duplicates silently**: any new test that needs to inspect Serilog's JSON console output (redirect `Console.Out` to a `StringWriter`, restore in `finally`, filter/parse lines) should use the `ConsoleLogCapture` helper (`CaptureAsync`/`ParseLogLinesWithTraceId`) rather than re-inlining the pattern. It grew to 3 near-identical copies within `EventLedger.Gateway.Tests` before being extracted during issue #4's `/simplify` pass — but a 4th copy in `EventLedger.AccountService.Tests` (`AccountServiceLoggingTests.cs`) was missed in that pass and only caught by the follow-up `/workflow-review`. `ConsoleLogCapture` is `internal`, so it exists as a **deliberate, separate copy per test project** (`tests/EventLedger.Gateway.Tests/ConsoleLogCapture.cs` and `tests/EventLedger.AccountService.Tests/ConsoleLogCapture.cs`) — same rationale as `SqliteTempDbFixture`, since this codebase's test projects don't reference each other. Within one test project, always reuse the local copy; don't inline the pattern again. *(First seen: 2026-07-15; Account Service copy added: 2026-07-16)*
