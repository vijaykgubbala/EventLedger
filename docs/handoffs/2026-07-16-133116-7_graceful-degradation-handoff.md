---
issue: 7
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/7
branch: 7_graceful-degradation
base: master
plan: docs/plans/7_graceful-degradation-plan.md
---

# Handoff: Story 6 — Graceful Degradation (review-fix round)

## Release Notes

This is a follow-up handoff for the same story
([docs/handoffs/2026-07-16-125738-7_graceful-degradation-handoff.md](2026-07-16-125738-7_graceful-degradation-handoff.md)
covers the original implementation). Since that handoff, `/workflow-review`
ran five review agents against PR #18 and found seven findings — four
warnings, three suggestions, zero critical. Five were fixed with new
commits on this same branch; two were recorded as intentionally skipped.
The disposition record for every finding lives in
[docs/reviews/7_graceful-degradation.json](../reviews/7_graceful-degradation.json).

The most consequential fix: review-security and review-dotnet
independently caught that the new balance endpoint's `accountId` was
interpolated unescaped into the outbound Account Service request URL. A
crafted `accountId` containing `?` would truncate the path before
`/balance` and silently redirect the request to the Account Service's
full-account-details route instead — returning more data through the
Gateway's balance-only passthrough than that endpoint is supposed to
expose. Fixed with `Uri.EscapeDataString`, applied to both the new
balance handler and the pre-existing (issue #2/#6-era) `SubmitEventHandler`
call site, which had the identical bug. A new regression test captures
the outbound request URI directly and proves the escape holds.

The remaining fixes were smaller: `BalanceQueryHandler` now logs Account
Service failures at Warning level (it previously swallowed them silently,
unlike the equivalent code path in `SubmitEventHandler`); the duplicated
`503 account_service_unavailable` JSON envelope between the two
controllers was extracted into one shared helper; a previously-untested
branch of the failure-classification logic (Polly's own rejection
exceptions, not just raw `HttpRequestException`) now has a test; and a
test double (`StubHttpClientFactory`) that had drifted into two
near-identical private copies was consolidated into one shared file.

Two findings were deliberately left as-is: a maintainability suggestion
to extract the Account-Service-call classification logic itself (shared
between `SubmitEventHandler` and `BalanceQueryHandler`) was declined,
because this exact tradeoff — generalizing shared plumbing at only two
call sites — was already evaluated and explicitly rejected twice earlier
in this same story (the brainstorm's "Approach 3," and the `/simplify`
pass's altitude-angle review), both citing this project's stated
YAGNI stance. A testing suggestion to add a dedicated timeout/circuit-
breaker test for the new balance endpoint was also declined, since it
reuses the exact same named `HttpClient` and resilience pipeline already
exhaustively tested against `POST /events` — a duplicate test would
exercise DI wiring, not new behavior.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| URL escaping fix (`BalanceQueryHandler.cs`, `SubmitEventHandler.cs`) | Small but touches a pre-existing, already-shipped code path (`SubmitEventHandler`) outside this story's original diff | Whether escaping `accountId` changes behavior for any already-valid `accountId` (it shouldn't — `Uri.EscapeDataString` is a no-op on unreserved characters, and every existing `accountId` in tests/fixtures is alphanumeric-with-hyphens) | New regression test captures the outbound `HttpRequestMessage.RequestUri` directly and asserts the escaped path; full Gateway suite (63 tests, including all pre-existing `SubmitEventHandler`/`EventsController` tests) passes unchanged |
| `BalanceQueryHandler` logging addition | Trivial — adds an `ILogger` dependency and two log calls, no behavior change to what's returned to callers | N/A | DI resolves `ILogger<BalanceQueryHandler>` automatically (ASP.NET Core's built-in logging registration); no new registration needed, confirmed by `AccountsControllerTests` passing unchanged |
| Shared `AccountServiceUnavailable()` extension method | Small — pure refactor, no new logic, just deduplicated an existing literal | Whether the response shape is byte-for-byte identical to before | All existing `503`-path tests in both `EventsControllerTests` and `AccountsControllerTests` pass unchanged |
| Test-only changes (new branch-coverage test, `StubHttpClientFactory` consolidation) | None to production behavior | Whether the `SubmitEventHandlerTests.cs` refactor (touching a pre-existing file outside this story's original scope) preserved every existing test's behavior | Full `SubmitEventHandlerTests` suite (7 tests) re-run and passing unchanged after the consolidation |

## Test Coverage

### Planned vs Actual

This round's tests were driven by review findings, not the original
plan's Testing Strategy (already fully satisfied per the prior handoff).
See [docs/reviews/7_graceful-degradation.json](../reviews/7_graceful-degradation.json)
for the full finding-by-finding record; summarized:

| Finding | Status | Notes |
|---|---|---|
| F1: unescaped `accountId` in outbound URL | addressed | New test: `BalanceQueryHandlerTests.GetBalanceAsync_AccountIdContainsReservedCharacters_EscapesBeforeSendingRequest` |
| F2: missing failure logging | addressed | No new test — logging isn't asserted at this layer anywhere in the codebase; mirrors `SubmitEventHandler`'s existing (also untested-for-log-content) precedent |
| F3: duplicated error envelope | addressed | Pure refactor; existing tests re-verify unchanged behavior |
| F4: duplicated classification logic | ignored | See Release Notes — already-declined tradeoff |
| F5: no dedicated resilience test for balance endpoint | ignored | See Release Notes — would test DI wiring, not new behavior |
| F6: untested `ExecutionRejectedException` branch | addressed | New test: `BalanceQueryHandlerTests.GetBalanceAsync_ResiliencePipelineRejectsCall_ReturnsUnavailable` |
| F7: duplicated `StubHttpClientFactory` | addressed | Consolidated into `tests/EventLedger.Gateway.Tests/StubHttpClientFactory.cs` |

### What's Not Tested

Same as the prior handoff — no new gaps introduced by this round. The
`AddScoped<BalanceQueryHandler>()` DI registration line remains untested
directly but exercised indirectly by every `AccountsControllerTests` case.
