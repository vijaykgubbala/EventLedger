# README Finalization (Story 9)

**Issue:** #10

## Context

Builds on
[docs/brainstorms/10_readme-finalization-brainstorm.md](../brainstorms/10_readme-finalization-brainstorm.md).
`README.md` has one remaining `TODO` stub (Setup) and one stale status
callout referencing it; the other two originally-stubbed sections
(Running the services, Running the tests) were already completed by
issues #8 and #9. This plan implements the brainstorm's recommended
Approach 2: fix the Setup stub and stale callout, then run a light
accuracy scan of the remaining four README sections against current
source rather than assuming they're still correct — plus the
commit-history narrative audit the issue's acceptance criteria require.

## Relevant Learnings

No `docs/solutions/` entries apply to this topic (the two that exist —
`layering/healthcontroller-dbcontext-injection` and
`observability/metrics-middleware-exception-safety` — are unrelated
code-level fixes, not documentation work).

One finding from this plan's own research, not yet in the brainstorm:
spot-checking the API section against the actual controllers
(`src/EventLedger.Gateway/Controllers/AccountsController.cs`) found the
README's Gateway endpoint table is missing a row — `GET
/accounts/{accountId}/balance` (the Gateway's balance passthrough,
added during issue #6) is a real, working endpoint used throughout
`docs/verification-guide.md` but was never added to the README's API
table. This is exactly the kind of gap Approach 2's accuracy scan was
meant to catch, and is folded into Phase 2 below rather than raised as
a separate question — it falls directly under the brainstorm's already-
agreed "fix anything found" scope.

## Implementation Steps

### Phase 1: Fix the Setup stub and the stale status callout

- [x] Replace the `## Setup` section's `TODO` (README.md lines 120-125)
  with two clear prerequisite paths, per the brainstorm's Q2 decision:
  - Primary path: Docker + Docker Compose only (no local .NET SDK
    needed) — this is what `docker compose up --build` in "Running the
    services" requires.
  - Secondary path: .NET SDK 8.0, needed only for the manual `dotnet
    run` path already documented in "Running the services" and for
    `dotnet test` in "Running the tests". Confirmed from both
    `.csproj` files' `<TargetFramework>net8.0</TargetFramework>` — no
    `global.json` pins a specific patch version, so no need to name one.
- [x] Remove the status callout (README.md lines 9-12) entirely, per the
  brainstorm's Q3 decision — once Setup is filled in, nothing in the
  README is stubbed and the callout has no remaining purpose.

### Phase 2: Accuracy scan of the remaining four sections

- [x] Fix the API section (README.md lines 65-86): add the missing
  Gateway row `GET /accounts/{accountId}/balance` — "Get the current
  balance for an account (passthrough to the Account Service)" —
  sourced from `src/EventLedger.Gateway/Controllers/AccountsController.cs`.
- [x] Re-check the Tech stack table (lines 49-63), Resiliency section
  (lines 88-101), and Assumptions & bonus scope section (lines 103-118)
  against current source (`architecture/`, both `.csproj` files,
  `standards/`). Fix anything else found; if nothing else is found,
  leave those sections unchanged — do not rewrite working content for
  its own sake.

### Phase 3: Commit-history narrative audit

- [x] Run `git log --merges --oneline master` and confirm each of the 9
  numbered stories (issues #2-#9, excluding the tooling-only PR #12 and
  the 3_service-separation/2_core-functionality bootstrap pair) has
  exactly one story-branch merge commit, in sequence, with no squashing.
  This plan's own research already confirmed this pattern holds for
  PRs #11 through #20 — this step re-confirms it one more time at
  execution and records the result in this plan's Testing Strategy
  section below (audit-only, no code/doc changes expected from this
  step per the brainstorm's Q4 decision).

  **Result (re-confirmed against current `master`, HEAD `0f165cc`):**
  clean. Each numbered story has exactly one story-branch merge commit
  (`583286b` #3, `3802019` #2, `8f7ee89` #4, `e40fc08`+`56fab41` #5,
  `d1732b7` #6, `74322ca` #7, `9c72bed` #8, `c243fe0` #9), in ascending
  order, no squash merges. Issue #5's two merges are a documented
  review-fix follow-up round, not squashing or cross-story mixing.
  `08f5791` (#12, tooling-only) and `0f165cc` (#22, issue #21) are
  present as expected — neither is one of the 9 numbered stories, so
  neither affects this finding.

## Testing Strategy

### Test Environment

Not applicable in the usual xUnit sense — this plan adds no C# code and
touches only `README.md`. "Testing" here is a direct read-through
verification: every claim in the finalized README is checked against
either running the actual command (Setup/Running the services/Running
the tests paths) or reading the actual source file it describes (API
table, Tech stack, Resiliency).

### Test Cases

- **Description**: The Setup section's two prerequisite paths are
  individually sufficient — Docker Compose path needs nothing but
  Docker; the manual/test path needs .NET SDK 8.0 and nothing more.
  **Type**: Manual, live (re-run `docker compose up --build` and
  `dotnet test` against a clean checkout mentally cross-checked against
  what's actually required, not re-verified as a fresh clone in this
  pass since issues #8/#9 already did this end to end).
  **Phase reference**: Phase 1.
- **Description**: The API table lists every route that actually exists
  on both controllers, with no extras and no omissions.
  **Type**: Static, source cross-check (`AccountsController.cs` x2,
  `EventsController.cs`).
  **Edge cases**: the Gateway's balance-passthrough row, already known
  missing (see Relevant Learnings).
  **Phase reference**: Phase 2.
- **Description**: Every merged story branch (issues #2-#9) appears as
  exactly one merge commit in `git log --merges --oneline master`, in
  ascending order, no squash merges.
  **Type**: Manual, live (`git log` against the actual current
  `master`, not the earlier partial spot-check from the brainstorm).
  **Phase reference**: Phase 3.

## Decisions Made

- **Proceed independently of PR #22** (issue #21, the verification-guide
  story) — issue #10's "preceding stories" dependency refers to the 9
  originally-numbered stories, all merged; #21 is later, out-of-band
  scope. *(Brainstorm Q1)*
- **Setup section shows two clear paths** rather than one unified
  prerequisite list, so a reader who only wants Docker Compose isn't
  told to install a local .NET SDK they don't need. *(Brainstorm Q2)*
- **Remove the stale status callout entirely** rather than correcting
  its wording, since it will have no remaining purpose once Setup is
  filled in. *(Brainstorm Q3)*
- **Commit-history review is audit-only**, recorded as a test case in
  this plan rather than a separate `docs/` writeup — the brainstorm's
  spot-check already found the history clean, so this is a
  confirmation step, not remediation work. *(Brainstorm Q4)*
- **`CancellationToken` propagation pattern**: not applicable — this
  plan adds no async C# call chains, only documentation.

### Known Constraints

- This plan touches only `README.md` (and possibly nothing else, if
  Phase 2's scan finds no further gaps beyond the already-known API
  table omission) — no `architecture-guide` pre-flight is required
  since no `src/` code is touched.
