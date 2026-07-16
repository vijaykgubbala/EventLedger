---
title: An issue can legitimately span two merge commits, not always exactly one
date: 2026-07-16
related: [../reviews, ../../.claude/skills/workflow-review/SKILL.md, ../../.claude/skills/workflow-handoff/SKILL.md]
---

# An issue can legitimately span two merge commits, not always exactly one

## Pattern

`workflow-review`'s Interactive Walkthrough can produce new fix commits
on a story's branch *after* that branch's PR has already been opened —
and if the PR gets merged (e.g. by the user, externally via the GitHub
UI) before those fix commits are pushed, the commits are stranded on
the local branch, orphaned relative to the now-merged PR. The correct
recovery is to push the branch and open a **second, small PR** against
the same branch/issue to bring the stranded commits into `master` — not
to force-push, rewrite history, or silently fold them into an unrelated
future change.

Net effect: one issue, one branch, but **two merge commits into
master**. This has now happened at least twice in this repo's history:

- **Issue #5** (`5_observability`): PR #15 (main story) + PR #16
  (`5_observability-handoff-fix`, a review-fix follow-up).
- **Issue #21** (`21_verification-guide`): PR #22 (main story, merged
  with two review-fix commits still unpushed) + PR #23 (those two
  commits, `1a71b08` and `ce79d18`, pushed and merged separately ~7
  minutes later).

## Guidance

- When auditing commit history for "does every issue trace to exactly
  one merge?" (e.g. the commit-history narrative check in issue #10's
  plan), **expect 1-2 merge commits per issue, not always exactly 1**.
  Two merges for the same branch name is not evidence of squashing or
  cross-story mixing — check whether the second merge's commits are
  genuinely a review-fix follow-up for the same issue before flagging
  it as an anomaly.
- The underlying trigger is a timing gap between "workflow-review
  writes a fix commit locally" and "that commit gets pushed" — if a PR
  merge (by any party, not just the agent) can happen in that gap, the
  split becomes possible. There's no tooling fix planned for this in
  EventLedger's scaled-down workflow (a solo-contributor repo doesn't
  need one) — it's accepted as a known, self-consistent shape of the
  process, not a bug to eliminate.
- If you notice a review-review-round's fix commits are sitting
  unpushed on a branch whose PR is already merged, the fix is exactly
  what happened for issue #21: push the branch, open a small follow-up
  PR referencing what it brings in and why, merge it. Don't try to
  retroactively rewrite the already-merged PR's history.

## Examples

- Issue #5: PR #15 + PR #16 (`5_observability-handoff-fix`).
- Issue #21: PR #22 + PR #23 (`fix(docs): reconcile verification-guide.md
  review findings (F1/F2)`) — discovered and recovered mid-way through
  issue #10's `workflow-execute` run, when branching for issue #10
  required pulling latest `master` and the F1/F2 fixes were found
  missing from it.
- The recovery itself is documented in issue #10's conversation history
  (not a separate file) — surfaced to the user via `AskUserQuestion`,
  which chose "push + open a small follow-up PR."
