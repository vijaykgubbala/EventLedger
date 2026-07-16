# Brainstorm: README Finalization (Story 9)

**Date:** 2026-07-16
**Issue:** #10

## Problem Statement

`README.md` has three sections the original scaffold pass stubbed with
`TODO`: Setup, Running the services, Running the tests. Issues #8 and #9
already filled in the latter two for real. Only **Setup** is still a
literal `TODO` stub, and the top-of-file "Status" callout that
references those stubs is itself now stale (it claims the test-run
command is still TODO, which is false). The issue's acceptance criteria
also require an end-to-end commit-history review confirming the repo's
history reads as a per-story narrative, not a squash.

## Codebase Context

- **README.md (198 lines)** — all five required sections exist
  structurally (Architecture overview, Tech stack, API, Resiliency,
  Assumptions & bonus scope) plus Setup / Running the services / Running
  the tests.
  - **Setup (lines 120-125): still the literal TODO stub** from the
    original scaffold — "to be filled in once the ASP.NET Core projects
    exist." The projects have existed since issue #3.
  - **Running the services (127-168): genuinely complete** — real
    `docker compose up --build`, port numbers, curl health checks,
    `docker compose down`, and a manual `dotnet run` fallback path.
  - **Running the tests (170-199): genuinely complete** — `dotnet test`,
    "94 tests total," an 8-row requirement→test-file coverage table, and
    a link to `docs/verification-guide.md`.
  - **Status callout (lines 9-12): stale.** Says *"Setup prerequisites
    and the test-run command below are still stubbed with TODO"* — only
    Setup actually is.
- **Real prerequisites**, confirmed from source:
  - `.NET SDK 8.0` — both `.csproj` files target `net8.0`; no
    `global.json` pins a specific patch version.
  - Docker + Docker Compose — both Dockerfiles multi-stage build from
    `mcr.microsoft.com/dotnet/sdk:8.0` to
    `mcr.microsoft.com/dotnet/aspnet:8.0`, plus `curl` for healthchecks.
  - Local .NET SDK is only needed for `dotnet run`/`dotnet test` — the
    primary `docker compose up` path needs nothing but Docker.
- **AIDLC-USAGE-GUIDE.md's "GitHub workflow" section** defines the
  narrative-history requirement: each story is a branch
  (`<N>_<slug>`) with multiple small, phase-labeled commits, merged via
  a real merge commit (not a GitHub squash-merge) — history preserves
  every incremental step as a readable sequence.
- **Git log sanity check** (issues #6-#9 on `master`): each story is a
  distinct merge commit preceding a clean run of phase-labeled commits,
  review-fix commits, then handoff/disposition docs. No evidence of
  squashing or cross-story mixing.
- Branch `21_verification-guide` (issue #21, not one of the 9 numbered
  stories) is open as PR #22, sitting on top of the issue #9 merge —
  confirmed out of scope for issue #10's "preceding stories" dependency
  per this brainstorm's Q&A below.

## Q&A Decisions

**Q1: Should README work for issue #10 wait for PR #22 to merge first?**
A: Proceed independently. Issue #10's stated dependency is "all
preceding stories" — the 9 originally-numbered stories, all already
merged. #21 is later, out-of-band scope and can merge on its own
schedule.

**Q2: How thorough should the Setup section be?**
A: Two clear paths — Docker + Docker Compose as the only requirement for
the primary `docker compose up` path, with .NET SDK 8.0 called out
separately only as needed for the local `dotnet run`/`dotnet test` path.

**Q3: Should the stale Status callout be corrected or removed?**
A: Removed entirely. Once Setup is filled in, nothing in the README is
stubbed anymore — the callout has no remaining purpose.

**Q4: How should the commit-history review acceptance criterion be
satisfied?**
A: Audit-only. Run `git log` across the merged story branches as a
verification step and record the finding in the plan's testing
strategy — no code/doc changes needed since the audit already found the
history clean.

## Proposed Approaches

### Approach 1: Targeted fix only

Fill in Setup, remove the stale Status callout, run the commit-history
audit. Leave the other four README sections (Architecture overview,
Tech stack, API, Resiliency, Assumptions) untouched, on the assumption
prior review passes already kept them accurate.

**Pros:**
- Matches the issue's small (~0.25h) effort estimate exactly.
- Minimal diff, easy to review.

**Cons:**
- The acceptance criteria literally say "all five README sections ...
  accurate against what was actually built" — this approach asserts
  that without checking it this pass.

### Approach 2: Targeted fix + light accuracy scan

Same targeted fix as Approach 1 (Setup, callout removal, commit-history
audit), plus a quick read-through of the other four sections against
current source (`architecture/`, the two `.csproj` files, `standards/`)
to catch any obvious drift — a scan, not a rewrite. Fix anything found;
otherwise leave as-is.

**Pros:**
- Directly satisfies the "accurate against what was actually built"
  wording of the acceptance criteria, rather than assuming it.
- Still cheap — reading four already-short sections against source
  takes minutes, not a rewrite.

**Cons:**
- Slightly more time than the issue's effort estimate, though still
  well under the original 4-hour per-story budget.

## Recommendation

**Approach 2.** The extra cost is a few minutes of reading, and it
converts an assumption ("the other sections are probably still fine")
into a verified fact — which is exactly what an evaluator-facing README
finalization story should do. If the scan turns up nothing, the diff
ends up identical to Approach 1 anyway.

## Related Docs

- [docs/plans/8_docker-compose-plan.md](../plans/8_docker-compose-plan.md) — wrote the "Running the services" section this story inherits.
- [docs/plans/9_automated-tests-plan.md](../plans/9_automated-tests-plan.md) — wrote the "Running the tests" section this story inherits.
- [AIDLC-USAGE-GUIDE.md](../../AIDLC-USAGE-GUIDE.md) — source of the commit-history narrative requirement.
