# docs/ Governance

This directory is Claude's institutional memory for Event Ledger. Claude
owns it, organizes it, and reads it in future sessions. Every file here
exists to make future Claude sessions — and future you — faster and more
accurate. Adapted from a larger reference workflow; trimmed to what a
single-repo, solo-contributor project actually needs (no multi-subrepo
routing — everything below lives directly under `docs/`, never a
per-subrepo `docs/` root).

## Folder Structure

| Directory | Purpose | Lifecycle |
|---|---|---|
| `brainstorms/` | Structured problem exploration before planning (`workflow-brainstorm`) | Issue-anchored, immutable |
| `plans/` | Research-backed implementation plans with checkboxes (`workflow-plan`) | Issue-anchored, checked off during execution |
| `handoffs/` | Implementation handoff — release notes, risk analysis, test coverage (`workflow-handoff`) | Dated, immutable |
| `reviews/` | Code review artifacts (JSON) with finding dispositions (`workflow-review`) | Issue-anchored, updated during walkthrough |
| `solutions/` | Solved problems and decisions organized by category (`workflow-compound`) | Dated, immutable |
| `patterns/` | General patterns, conventions, and recurring themes — **already exists**, seeded from the initial scaffold pass | Dated, can be updated when new examples emerge |
| `workflow-recommendations/` | Pending improvements to the workflow tooling itself | Status-tracked (pending/completed/stale) |

## Principles

- **Small and focused.** One topic per file. Two distinct problems means two files.
- **Scannable.** Frontmatter, headers, bullets. You scan before you read.
- **Self-contained.** Each file makes sense alone, but cross-references related docs.
- **Progressive disclosure.** This file explains the structure. Subdirectories are discoverable via glob. Individual files contain focused content.

## Cross-Referencing

Link related docs across subdirectories using relative paths:
- Brainstorms link to the plans they led to
- Plans link to brainstorms they drew from and solutions that informed them
- Solutions link to the plans and reviews that produced them
- Patterns reference related solutions

Format: `See also: [title](../plans/6_resiliency-plan.md)`

## Patterns

Patterns capture general conventions, recurring themes, and reusable
approaches observed during work. They differ from solutions (which
document one specific problem/fix). Write a pattern when an insight is
general enough to apply across multiple future tasks — this directory
already has two seed examples:
[2026-07-15-idempotency-key-race.md](patterns/2026-07-15-idempotency-key-race.md)
and
[2026-07-15-cancellation-token-propagation.md](patterns/2026-07-15-cancellation-token-propagation.md).

Pattern files: `docs/patterns/YYYY-MM-DD-<descriptive-slug>.md`. Use clear,
descriptive names that future searches will match.

## Relationship to GitHub Issues

This repo tracks work as GitHub Issues (`#2`–`#10`, one per story, grouped
into phase milestones). The `workflow-*` skills below take an **issue
number** as their argument and write their output here, under `docs/` —
the issue itself stays the short, stable record (title, acceptance
criteria, milestone); the deep artifact (brainstorm reasoning, the full
plan, the review findings) lives here and gets linked back via a comment
on the issue.

**Naming convention**: brainstorm, plan, and review filenames — and the
`workflow-execute` branch they're built on — are all anchored to the
issue number, not a date: `docs/brainstorms/<issue-id>_<slug>-brainstorm.md`,
`docs/plans/<issue-id>_<slug>-plan.md`, `docs/reviews/<issue-id>_<slug>.json`,
and branch `<issue-id>_<slug>` (e.g. `2_core-functionality`). The issue
number is the permanent identifier; a date only says when the file was
written, which matters less than which story it belongs to. `handoffs/`,
`solutions/`, and `patterns/` stay date-prefixed (`YYYY-MM-DD-...`) —
handoffs and solutions aren't always tied to a single issue, and patterns
are explicitly cross-cutting.

## Relationship to `architecture/` and `standards/`

`docs/` is where work-in-progress reasoning and history live. `architecture/`
and `standards/` are where **settled decisions** live — see
[../architecture/README.md](../architecture/README.md) and
[../governance/architecture-docs-edit-gate.md](../governance/architecture-docs-edit-gate.md).
A brainstorm or plan that changes a documented architecture decision must
update the owning `architecture/*.md` file in the same change, not just
record the new reasoning here and leave the two to drift.
