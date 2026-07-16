# Automated tests — full requirements checklist coverage

**Issue:** #9

## Context

Builds on the brainstorm at
[docs/brainstorms/9_automated-tests-brainstorm.md](../brainstorms/9_automated-tests-brainstorm.md).
A dedicated audit against every TEST-1..8 criterion in
[.claude/agents/review-testing.md](../../.claude/agents/review-testing.md)
found 7 of 8 already fully covered by tests that landed during issues
#2, #4, #6, and #7. The one gap: `EventValidator.cs` already correctly
rejects a malformed (present but unparseable) `eventTimestamp` — confirmed
by reading the source directly (`DateTimeOffset.TryParse` failure adds a
`ValidationFailure`) — but no test exercises that path. This plan closes
that one test gap, updates the checklist doc that let it go uncaught, and
completes README's already-`TODO`-stubbed "Running the tests" section
with a coverage-mapping table.

## Relevant Learnings

No `docs/solutions/` entries apply to this topic. Directly applicable
patterns: [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md)
(already followed everywhere — confirmed zero `InMemory` usages
repo-wide during the brainstorm's audit, nothing to change here).
[docs/reviews/2_core-functionality.json](../reviews/2_core-functionality.json)
and [docs/reviews/6_resiliency.json](../reviews/6_resiliency.json) are
the two prior reviews whose findings are what closed most of TEST-1..8
already — referenced for context, not reopened.

No architecture pre-flight was run: this plan adds no new production
code under `src/` (`EventValidator.cs`'s malformed-timestamp rejection
already exists and is correct) — every change is test-only or docs-only.

## Implementation Steps

### Phase 1: Close the TEST-4 validation gap

- [x] Write test: `Validate_MalformedEventTimestamp_Fails` in
  `tests/EventLedger.Gateway.Tests/EventValidatorTests.cs`, mirroring the
  existing `Validate_AmountNotGreaterThanZero_Fails` shape (lines 35-43):
  call `_validator.Validate(eventId, accountId, type, amount, currency, "not-a-date")`
  using the same `ValidPayload()` helper for the other fields, assert
  `Assert.Contains(failures, f => f.Field == "eventTimestamp")`. This is
  distinct from the existing `Validate_EachRequiredFieldMissingIndividually_FailureNamesThatField`
  theory (which only covers `eventTimestamp == null`, not a present-but-
  malformed value) — a new `[Fact]`, not a new `[InlineData]` on that
  theory.
- [x] Write test: `PostEvents_MalformedEventTimestamp_Returns400WithErrorShape`
  in `tests/EventLedger.Gateway.Tests/EventsControllerTests.cs`, mirroring
  the existing `PostEvents_InvalidType_Returns400WithErrorShape` shape
  (lines 87-97): POST with `eventTimestamp = "not-a-date"` and all other
  fields valid, assert `HttpStatusCode.BadRequest` and
  `body.Error == "validation_error"`. No implementation step follows
  either test — `EventValidator.cs` already rejects this input correctly;
  both tests are expected to pass immediately once written, proving
  already-correct behavior rather than driving new code (same pattern as
  issue #7's RES-6/RES-7 verification tests).

### Phase 2: Update the review-testing.md checklist doc

- [x] Update `.claude/agents/review-testing.md`'s "Required checklist"
  item 4 (currently lines 25-27) to name the malformed-`eventTimestamp`
  case explicitly, alongside the 3 cases already listed (missing required
  field, `amount <= 0`, invalid `type`) — closes the doc/reality mismatch
  identified in the brainstorm (this checklist's narrower wording vs.
  `standards/api.md`'s fuller 4-rule validation list) so future
  `/workflow-review` passes catch this class of gap instead of silently
  missing it.

### Phase 3: README test coverage documentation

- [x] Fill in `README.md`'s existing `**TODO:**` under "Running the
  tests" (currently line 172) with the real `dotnet test` command (the
  fenced block below it is already correct, just currently unreachable
  behind the `TODO` label).
- [x] Add a coverage-mapping table immediately after, mapping each
  TEST-1..8 criterion (per `.claude/agents/review-testing.md`'s updated
  wording from Phase 2) to its exact covering test file(s) and method
  name(s) — the full mapping is already known from the brainstorm's
  audit (see its Codebase Context section for every TEST-N's exact
  file:method references). Do not touch the existing "Setup" `TODO`
  section — out of scope, reserved for issue #10.

### Phase 4: Final verification

- [x] Run `dotnet test` from repo root. Confirm the full suite passes
  with the 2 new tests included (92 existing + 2 new = 94 total), zero
  failures, zero undocumented setup steps.
  - Confirmed: 94/94 passed (29 Account Service + 65 Gateway), 0 failed.
- [x] Run `dotnet format --verify-no-changes --no-restore`, confirm exit
  code 0.
  - Confirmed: exit code 0, no formatting drift.

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../standards/backend-architecture.md#test-project-layout).
Both new tests are unit/HTTP-level validation tests with no database
interaction (`EventValidator` is pure logic; the controller-level test
uses the existing `WebApplicationFactory` + `SqliteTempDbFixture` pattern
already established in `EventsControllerTests.cs`, but never reaches a
DB write since validation fails first) — no new fixture or test-double
infrastructure needed.

### Test Cases

- **Description**: `EventValidator.Validate` rejects a present-but-
  malformed `eventTimestamp` (e.g. `"not-a-date"`), naming the
  `eventTimestamp` field in the failure.
  **Type**: Unit. **Edge cases**: distinguishes "malformed" from
  "missing" (already covered by the existing theory) — this test must
  use a non-null, non-empty, syntactically-invalid string.
  **Phase reference**: Phase 1.
- **Description**: `POST /events` with a malformed `eventTimestamp`
  returns `400` with the standard `validation_error` envelope.
  **Type**: Integration (HTTP, via `WebApplicationFactory`).
  **Phase reference**: Phase 1.

## Decisions Made

- **Both tests are `[Fact]`/standalone, not added as `[InlineData]` to
  an existing theory.** The existing
  `Validate_EachRequiredFieldMissingIndividually_FailureNamesThatField`
  theory specifically tests the *missing* (`null`) case for every field;
  a malformed-but-present value is a semantically different input
  category, and shoehorning it into that theory (which asserts a generic
  `f.Field == missingField` check across all 6 fields) would either
  require restructuring the theory or produce a misleading test name.
  Matches how `Validate_AmountNotGreaterThanZero_Fails` (a different
  invalid-but-present case) is already its own separate `[Fact]`, not
  folded into the missing-field theory.
- **No production code changes.** Confirmed by reading
  `EventValidator.cs` directly during brainstorm research — the
  `DateTimeOffset.TryParse` check and its `ValidationFailure` already
  exist and are correct. This story is pure gap-closure in test
  coverage plus documentation, consistent with the brainstorm's
  Approach 1 recommendation.

### Known Constraints

None beyond what's already established project-wide (real file-based
SQLite for anything touching persistence — not applicable here, since
neither new test reaches a DB write).
