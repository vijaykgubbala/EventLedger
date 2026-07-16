# Brainstorm: Automated tests — full requirements checklist coverage

**Date:** 2026-07-16
**Issue:** #9

## Problem Statement

The assignment requires automated tests proving idempotency, out-of-order
handling, balance computation, validation, resiliency, and trace
propagation actually work — runnable via `dotnet test` with no
undocumented setup — plus at least one full Gateway→Account Service
integration test. The issue's acceptance criteria list this as TEST-1
through TEST-8, citing `.claude/agents/review-testing.md` as the
authoritative checklist this story must satisfy.

## Codebase Context

- **`.claude/agents/review-testing.md`** defines the authoritative
  checklist, slightly more precisely worded than the issue body's
  TEST-1..8 list. Notably, its TEST-4 wording names only 3 validation
  rejection cases (missing required field, `amount<=0`, invalid `type`),
  while **`standards/api.md`**'s own "Validation rules" section documents
  a 4th: a present-but-malformed `eventTimestamp`.
- A full audit against every TEST-N criterion (via a dedicated research
  pass) found **7 of 8 already fully covered** by tests that landed
  during issues #2, #4, #6, and #7 — several added specifically in
  response to prior `/workflow-review` findings that named these exact
  gaps (e.g. the zero/all-credit/all-debit balance edge cases and the
  HTTP-level validation tests were both review-driven additions in
  `docs/reviews/2_core-functionality.json`).
- **TEST-1 (idempotency, real SQLite)**: fully covered.
  `SqliteTempDbFixture.cs` (both test projects) uses `UseSqlite` against
  a temp file — zero `InMemory` usages anywhere in the repo (confirmed by
  grep), matching the hard requirement in
  `docs/patterns/2026-07-15-idempotency-key-race.md`.
  `SubmitEventHandlerTests.cs` covers duplicate-`eventId` (existing
  record, Account Service not re-called), mismatched-payload resubmission
  (original wins), and a concurrent-same-`eventId` race (DB `UNIQUE`
  constraint). `GatewayToAccountServiceFullFlowTests.PostEvents_AccountServiceAlreadyConfirmedBeforeGatewayCrash_RetrySucceedsWithoutDoubleApplying`
  covers the cross-service case.
- **TEST-2 (out-of-order)**: fully covered.
  `EventsControllerTests.GetEventsByAccount_ReturnsArrayOrderedAscendingByEventTimestamp`
  (listing order) and
  `GatewayToAccountServiceFullFlowTests.PostEvents_MixedTransactionsSubmittedOutOfEventTimestampOrder_ResultingBalanceIsCorrect`
  (balance correctness) — both explicitly named by the checklist.
- **TEST-3 (balance incl. zero-transaction)**: fully covered.
  `BalanceQueryHandlerTests.cs` (Account Service) has
  `GetBalanceAsync_NoTransactionsForAccount_ReturnsZero` — confirmed to
  be the actual zero-transaction case, not just general balance math —
  plus mixed, all-credit, and all-debit cases.
- **TEST-4 (validation, each rejection case)**: **the one gap**.
  `EventValidator.cs` already validates a malformed `eventTimestamp`
  correctly (`DateTimeOffset.TryParse` failure → a `ValidationFailure`,
  confirmed by reading the source directly) — this is existing, correct
  production code, not a bug. But no test anywhere exercises that
  specific rejection path; every existing "missing `eventTimestamp`"
  test passes `null`, never a malformed non-empty string. The other 3
  cases (missing field, `amount<=0`, invalid `type`) are covered at both
  the unit level (`EventValidatorTests.cs`) and the HTTP level
  (`EventsControllerTests.cs`, asserting `400` + the standard error
  envelope).
- **TEST-5 (resiliency, circuit + 503)**: fully covered, more thoroughly
  than required. `EventsControllerTests.cs` has dedicated tests for the
  bounded-timeout case (asserting `CallCount==3`, the exact regression a
  critical finding in `docs/reviews/6_resiliency.json` caught and fixed),
  circuit-opens-and-fails-fast, and half-open/close recovery.
- **TEST-6 (trace propagation)**: fully covered.
  `GatewayToAccountServiceFullFlowTests.PostEvents_TraceparentPropagatesOverRealNetworkCall_SameTraceIdInBothServicesLogs`
  — uses a real Kestrel socket (not `TestServer.CreateHandler()`, which
  bypasses `DiagnosticsHandler`'s traceparent injection — see
  `docs/patterns/2026-07-15-diagnosticshandler-bypassed-by-custom-httpmessagehandler.md`),
  captures real console output from both processes, asserts exactly one
  distinct trace ID across ≥2 log lines.
- **TEST-7 (full integration)**: fully covered, exceeds "at least one" —
  `GatewayToAccountServiceFullFlowTests.cs` wires two real
  `WebApplicationFactory` instances together exactly as the issue's
  design reference specifies, across 4 separate tests.
- **TEST-8 (`dotnet test`, no undocumented setup)**: fully covered,
  verified by actually running it — `dotnet test` from repo root
  auto-discovers both test projects, 92/92 tests pass, zero setup steps
  (each fixture self-manages its own temp SQLite file).
- Scanning all 8 existing `docs/reviews/*.json` artifacts for any
  `"ignored"`/`"deferred"` disposition that maps to a TEST-N gap found
  **none** — every prior review's non-addressed findings are either
  genuinely out-of-scope (a redundant duplicate test, infra hygiene in
  issue #8's pending suggestions) or unrelated to this checklist.
- **`README.md`**'s "Running the tests" section is still `TODO`-stubbed
  (left that way deliberately in issue #8, which only completed "Running
  the services") — this is the natural, already-signposted place for a
  TEST-N → test-file:method coverage mapping to live.

## Q&A Decisions

**Q1: Where should the new malformed-`eventTimestamp` test go?**
A: Both levels — `EventValidatorTests.cs` (unit) and `EventsControllerTests.cs` (HTTP, `400` + error envelope) — mirroring how the other 3 validation rules are already tested at both layers.

**Q2: Should `.claude/agents/review-testing.md`'s checklist wording be updated to include the malformed-`eventTimestamp` case?**
A: Yes — closes the doc/reality mismatch that let this gap go uncaught through several prior `/workflow-review` passes, so future reviews catch this class of gap instead of silently missing it.

**Q3: Should this story produce a written test-coverage-audit artifact?**
A: Yes — complete README.md's already-`TODO`-stubbed "Running the tests" section with a TEST-1..8 → test file:method mapping table, giving an evaluator a direct, checkable answer instead of requiring them to search the suite themselves.

## Proposed Approaches

### Approach 1: Minimal gap-closure + audit documentation (Recommended)

Add the one missing validation test at both levels (Q1), update
`review-testing.md`'s checklist wording (Q2), and complete README's
"Running the tests" section with a coverage table mapping each TEST-N to
its exact covering test(s) (Q3). No other new production or test code —
everything else already passes the checklist.

**Pros:**
- Matches what the audit actually found: 7/8 already done, one small
  well-scoped gap.
- The coverage table directly satisfies the acceptance criteria's own
  framing ("correctness isn't just asserted in documentation") by making
  the proof traceable, not by writing more tests than the checklist
  needs.
- Keeps the diff proportional to the actual gap, consistent with this
  project's YAGNI stance.

**Cons:**
- Doesn't proactively test paths beyond the checklist's literal scope —
  acceptable, since expanding scope here would just be re-litigating
  decisions (see Approach 2).

### Approach 2: Broader test-hardening pass beyond the checklist

Also add tests for scenarios not strictly required — e.g. a dedicated
resiliency test for the balance endpoint (`GET /accounts/{id}/balance`),
or an OTel resource-attribute (`ConfigureResource`/`AddService`) test.

**Pros:**
- More coverage in absolute terms.

**Cons:**
- Both examples were already proposed and explicitly declined during
  prior `/workflow-review` passes (issue #7's F5, issue #4's resource-
  attribute suggestion) — re-adding them here would just reverse
  decisions already made with documented reasoning, not close a real gap
  in this story's actual checklist.
- Against this project's stated YAGNI stance for a story whose own
  acceptance criteria are already fully specified by TEST-1..8.

### Approach 3: Full cross-checklist doc audit

Beyond fixing `review-testing.md`'s one TEST-4 mismatch, systematically
compare every `review-*.md` agent's checklist against every `standards/`
doc to find other similar drift.

**Pros:**
- Might catch other latent doc/reality mismatches project-wide.

**Cons:**
- Scope creep well beyond this story's ~1h budget and its specific
  TEST-1..8 acceptance criteria — a reasonable idea in principle, but its
  own separate initiative, not something to fold into a story whose
  acceptance criteria are already fully enumerated.

## Recommendation

**Approach 1.** All three Q&A decisions point the same direction: close
exactly the one gap the audit found, at the granularity the checklist
already uses, and make the resulting coverage traceable in README rather
than expanding scope into territory (Approaches 2 and 3) that's either
already been explicitly declined or belongs to a different initiative.

## Related Docs

- [.claude/agents/review-testing.md](../../.claude/agents/review-testing.md)
- [standards/api.md](../../standards/api.md)
- [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md)
- [docs/reviews/2_core-functionality.json](../reviews/2_core-functionality.json)
- [docs/reviews/6_resiliency.json](../reviews/6_resiliency.json)
- [docs/plans/8_docker-compose-plan.md](../plans/8_docker-compose-plan.md) (left "Running the tests" `TODO` for this story)
