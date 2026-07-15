---
name: test-dotnet
description: Run the .NET test suite with coverage collection and flag any project under 80% coverage. Use when the user asks to run tests, check coverage, or verify the test suite passes.
allowed-tools: Bash, Read, Glob
---

Runs `dotnet test` for Event Ledger with coverage collection, and reports
results against the checklist in
[.claude/agents/review-testing.md](../../agents/review-testing.md).

## Steps

1. Confirm a `.sln` or test projects exist (`Glob **/*.Tests.csproj` or
   `**/*.sln`). If none exist yet, report that there's no test suite to
   run yet — this repo may still be in the docs-only scaffold stage (see
   [CLAUDE.md](../../../CLAUDE.md#scaffold-status)) — and stop.
2. Run:
   ```
   dotnet test --collect:"XPlat Code Coverage"
   ```
   from the repository root (or the `.sln` directory).
3. Locate the generated coverage report(s) (Cobertura XML under
   `**/TestResults/**/coverage.cobertura.xml`) and extract per-project
   line-coverage percentage.
4. **Flag any project with coverage below 80%** by name and percentage.
   80% is a floor, not a target — do not treat exactly-80% projects as a
   problem, and do not chase 100% by suggesting tests for trivial
   generated code (DTOs, `Program.cs` bootstrapping).
5. Cross-check which of the required checklist items in
   [.claude/agents/review-testing.md](../../agents/review-testing.md) have
   at least one passing test, based on test names/namespaces (idempotency,
   out-of-order, balance, validation, resiliency, trace propagation,
   integration). This is a lightweight sanity pass, not a substitute for
   running the `review-testing` agent for a full audit — mention that
   agent if deeper coverage analysis is warranted.

## Output

Report: pass/fail status, per-project coverage percentage (flagging
<80%), and any required checklist item with no obviously corresponding
test. If tests fail, show the actual failure output — don't paraphrase
away the assertion message.
