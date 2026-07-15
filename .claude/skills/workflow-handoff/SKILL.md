---
name: workflow-handoff
description: Produce the handoff artifact (Release Notes, Risk Analysis, Test Coverage) for the current branch after execution, then push the branch and open a GitHub PR. Runs between workflow-execute and workflow-review.
disable-model-invocation: true
argument-hint: "[issue-number]"
allowed-tools: Read, Write, Glob, Bash
---

# Handoff

You are producing the handoff artifact for a completed implementation,
then shipping it: pushing the branch and opening a GitHub PR. The
reference workflow this is adapted from stops at the artifact — PR
creation there is a separate, ADO-specific tool. **This project folds PR
creation into Handoff instead**, per an explicit decision: EventLedger has
no separate PR-management tooling, and a solo-contributor GitHub repo
doesn't need one — Handoff is simply the "ship this story" step.

## Step 0: Resolve the Issue

If `$ARGUMENTS` is empty, use the current branch's leading number
(`git branch --show-current` → first digit sequence). Resolve:
`gh issue view <N> --json number,title,body,state,url`.

If no issue resolves, continue using `$ARGUMENTS` as topic context — the
handoff file is still written; Step 6 (issue comment) and Step 7 (PR
issue-link) are skipped.

## Step 1: Determine Branch, Base, and Changed Files

```
CURRENT_BRANCH=$(git branch --show-current)
BASE_BRANCH=master
CHANGED_FILES=$(git diff $BASE_BRANCH...HEAD --name-only)
```

(EventLedger has one long-lived branch, `master` — no auto-detection tier needed the way a multi-branch hub repo requires.)

## Step 2: Locate the Plan File

Glob `docs/plans/*-plan.md`. Pick the most recent file whose `**Issue:**` line matches the resolved issue number; otherwise the most recent by `YYYY-MM-DD` prefix.

If no plan file is found, set `PLAN_PATH = none` and continue — the Test Coverage section degrades gracefully (Step 4). Do not treat this as an error.

## Step 3: Gather Inputs

1. If `PLAN_PATH != none`: extract the plan's `## Testing Strategy` section.
2. Filter `CHANGED_FILES` for test files (paths under `tests/`, matching `*Tests.cs`).
3. Get the change narrative: `git diff $BASE_BRANCH...HEAD --stat` and `git log $BASE_BRANCH..HEAD --oneline`.

## Step 4: Compose Handoff Content

```markdown
---
issue: <N or empty>
issue_url: <url or empty>
branch: <CURRENT_BRANCH>
base: <BASE_BRANCH>
plan: <relative path to plan, or "none">
---

# Handoff: <short title — issue title or branch slug>

## Release Notes

<Narrative prose. What changed, why, and for whom. Not a commit message — explain the change in plain language, as if for the take-home evaluator reading the PR.>

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| <area touched> | <small / medium / large + scope> | <what a reviewer should look at first> | <how the risk is reduced — tests, existing constraints, etc.> |

<One row minimum. Add a row for every distinct area touched.>

## Test Coverage

### Planned vs Actual

<If PLAN_PATH == "none", replace the table with:>

> ⚠ No plan file was found under `docs/plans/`. Reconciliation against a planned Testing Strategy is skipped. The narrative below describes coverage derived from the diff only.

<Otherwise:>

| Planned Test | Status | Notes |
|---|---|---|
| <test description from the plan's Testing Strategy> | written / skipped / changed | <one line> |
| (unplanned) <test added during execution that wasn't in the plan> | added | <one line> |

### What's Not Tested

<Narrative prose. Areas the diff touches with no test coverage, and why: framework-guaranteed, tested indirectly elsewhere, accepted gap with rationale, planned follow-up.>
```

## Step 5: Write the Handoff File

1. Create `docs/handoffs/` if it doesn't exist.
2. Filename: `YYYY-MM-DD-HHMMSS-<branch-slug>-handoff.md` (branch slug: lowercase, `/` → `-`).
3. Write the file. Confirm via Read.

## Step 6: Push and Open the PR

1. `git push -u origin <CURRENT_BRANCH>`
2. Build the PR body from the handoff content — Release Notes as the summary, a link to the full handoff file, and `Closes #<N>` if an issue was resolved:

```
gh pr create --base master --head <CURRENT_BRANCH> \
  --title "<issue title, or a concise summary if no issue>" \
  --body "$(cat <<'EOF'
<Release Notes section from the handoff>

Full handoff: docs/handoffs/<filename>

Closes #<N>
EOF
)"
```

3. Capture the returned PR URL.

## Step 7: Update the Issue

If an issue was resolved in Step 0:

```
gh issue comment <N> --body "Handoff written: docs/handoffs/<filename>. PR opened: <PR URL>. Ready for /workflow-review."
```

## Step 8: Suggest Next Step

Tell the user the PR is open (share the URL) and suggest running `workflow-review` next, before merging — findings from that review can still land as fix-up commits on the same branch/PR.

## Constraints

- Do not merge the PR. This skill opens it; merging is a separate, explicit decision — see [AIDLC-USAGE-GUIDE.md](../../../AIDLC-USAGE-GUIDE.md) for the open question on who/when merges.
- Do not pull raw `git diff` output into the handoff file — use `--stat` and commit summaries, not the full patch.
- If the plan file is missing, degrade gracefully (Step 4). Do not abort and do not post an issue comment about the missing plan.
- Handoffs are optional in the upstream reference this is adapted from, but **not optional here** — Step 6 is how a story's code actually reaches GitHub as a reviewable PR, so skipping this skill means the branch never ships.
