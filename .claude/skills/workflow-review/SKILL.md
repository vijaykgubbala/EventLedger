---
name: workflow-review
description: Orchestrate a code review by running all review-* agents in parallel, persist findings as a JSON artifact under docs/reviews/, and walk through them interactively. Use before merging a PR opened by workflow-handoff.
disable-model-invocation: true
argument-hint: "[issue-number | 'continue']"
---

# Review

You are a code review orchestrator. Your job is to analyze what changed
on the current branch, run the five `review-*` agents in parallel,
persist their findings to a durable artifact, and walk the user through
each one interactively. Scaled down from a multi-repo reference: base
branch is always `master` (no auto-detection tier needed), and severity
is the 3-tier vocabulary already defined in this repo's `review-*.md`
agents (`critical` / `warning` / `suggestion`) — not a 4-tier
`critical`/`high`/`medium`/`low` scheme.

## Argument Detection

- If `$ARGUMENTS` is empty or an issue number: proceed with Phase 0 (normal review flow), scoped to the current branch.
- If `$ARGUMENTS` is `continue`: enter **Continue Mode** below; skip Phases 0–4.

## Continue Mode

1. Glob `docs/reviews/*.json`. Read each and collect those with at least one finding where `disposition.status == "pending"`.
2. **No files** have pending items → tell the user, stop.
3. **Exactly one file** has pending items → use it automatically, show filename and pending count.
4. **Multiple files** have pending items → `AskUserQuestion` to pick one (filename + pending count per file).
5. Load the chosen artifact and display: filename, branch, total pending findings by severity.
6. Go directly to **Phase 5: Interactive Walkthrough**, processing only `pending` findings. Do not re-run agents or regenerate the summary.

## Phase 0: Gather Changes

1. Current branch: `git branch --show-current`. Base is always `master`.
2. Full diff: `git diff master...HEAD`. Changed files: `git diff --name-only master...HEAD`.
3. Categorize: new vs. modified vs. deleted; file types; which service(s) (`EventLedger.Gateway`, `EventLedger.AccountService`, both, or docs-only) are affected.
4. Create `docs/reviews/` if it doesn't exist.
5. **Set up `$REVIEW_DIR`**: `docs/reviews/.tmp/<branch-slug>/` (branch slug: `git branch --show-current | tr '/' '-' | tr '[:upper:]' '[:lower:]'`). `mkdir -p` it — this holds the shared diff/file list and is deleted in Cleanup.
6. Create the review artifact: filename `<branch-slug>.json` under `docs/reviews/` — since branches are now named `<issue-id>_<slug>`, the branch slug already is the issue-anchored identifier, so no separate timestamp prefix is needed. If the file already exists (a second review pass on the same branch), that's expected — see Continue Mode above for resuming it rather than starting fresh. Populate `metadata`: `timestamp`, `branch`, `commitSha` (`git rev-parse HEAD`), `issue` (from `$ARGUMENTS` if numeric, else the branch name's leading digit sequence, else `null`), `filesReviewed`. Empty `findings` array.

## Phase 1: Discover Review Agents

1. Glob `.claude/agents/review-*.md`.
2. Read each agent's `description` frontmatter field — every one already states a narrow trigger scope (correctness/idempotency logic, .NET/EF Core idiom, test checklist coverage, security, maintainability).
3. Select agents whose trigger matches the nature of the current changes. On a small branch (e.g. docs-only), some agents may have nothing to check — still run them; each is cheap and self-reports "no findings" rather than being skipped, so coverage stays uniform across every review.
4. Announce which agents will run.

## Phase 2: Run Reviews in Parallel

1. Write `$REVIEW_DIR/diff.patch` (full diff) and `$REVIEW_DIR/changed-files.txt` (one path per line).
2. For each selected agent, dispatch via the Task tool using its **own** `subagent_type` (e.g. `subagent_type: review-security`) — not `general-purpose` — so its frontmatter tool restrictions actually apply. Pass the absolute `$REVIEW_DIR` path. Instruct it to read `diff.patch`/`changed-files.txt` from there and return its `{"findings": [...]}` JSON as its final message, per its own Output Format section.
3. Run all selected agents in parallel.
4. For each agent's returned message: strip a leading/trailing markdown code fence if present, parse as JSON. If unparseable or empty, mark that agent `no-output` / `invalid-json`.
5. **Auto-retry once** for any agent that didn't return clean JSON. An agent still failing after its retry is recorded as failed — do not block on it.
6. Merge findings from agents that returned clean JSON into the artifact: assign each a unique ID, copy `severity`/`file`/`line`/`summary`/`detail` as-is (already using this repo's vocabulary, no severity remapping needed), set `disposition: {"status": "pending", "reasoning": null, "commit": null, "issue": null, "timestamp": null}`. Record which agents ran cleanly (`metadata.agentsRun`) vs. failed (`metadata.agentsSkipped`, with a reason). Write the artifact. Update `summary` counts per [artifact-schema.json](artifact-schema.json).

## Phase 3: Synthesize Findings

1. **Zero-findings branch — check first.** If every selected agent ran cleanly AND `summary.totalFindings == 0`: emit, run **Cleanup**, and stop.

   ```
   All <N> review agents completed. Zero findings. Branch is clean.

   Artifact: docs/reviews/<filename>
   ```

2. **Partial-merge banner**, if any agent failed verification even after retry:

   ```
   ⚠️ <N> review agent(s) failed to return valid findings, even after one retry: <agent: cause, ...>.
   Findings below reflect only the agents that completed cleanly.
   ```

3. Deduplicate overlapping findings (two agents flagging the same line for the same reason — common between `review-correctness` and `review-dotnet`, e.g. a dropped `CancellationToken`; keep the more specific one, note it was corroborated). Group by severity.
4. Present the report:

```
## Review Summary

**Artifact:** docs/reviews/<filename>
**Agents run:** [...]
**Agents skipped:** [...]

### Critical
- [id, file:line, summary]

### Warning
- [id, file:line, summary]

### Suggestion
- [id, file:line, summary]
```

5. If there are `critical` findings, strongly recommend addressing them before merging the PR.

## Phase 4: Interactive Walkthrough

For each `pending` finding, ordered critical → warning → suggestion, present it and prompt via `AskUserQuestion`:

```
**Finding #<id>** [<severity>] — <agent>
File: <file>:<line>

**Issue:** <summary>
**Detail:** <detail>
```

Options:

- **Fix this now** — analyze root cause, state the fix approach (1–3 sentences), write a failing test if applicable, implement, verify tests pass, then dispatch the `commit` skill for the fix. Record `disposition.status = "addressed"`, `reasoning` = summary of the fix, `commit` = the resulting SHA.
- **Skip — won't fix** — ask for reasoning (required). `disposition.status = "ignored"`.
- **Defer to later** — ask for reasoning (required) and optionally a follow-up GitHub issue number. `disposition.status = "deferred"`.
- **Fix all remaining** — run "Fix this now" for every pending finding in severity order; anything that can't be fixed programmatically gets deferred with reasoning explaining why.

Write the updated artifact after every disposition change.

### Completion

```
All findings reviewed and recorded in docs/reviews/<filename>

Summary:
- Addressed: <count>
- Ignored: <count>
- Deferred: <count>
- Still pending: <count>
```

If anything was addressed, suggest: "N findings were addressed with new commits. Consider running `/workflow-review continue` — a pass-1 fix occasionally introduces something a pass-2 review catches." This is a suggestion, not automatic.

Then run **Cleanup**.

## Cleanup

1. Delete `$REVIEW_DIR` recursively (`docs/reviews/.tmp/<branch-slug>/`) — only the merged artifact at `docs/reviews/<filename>` remains.
2. Delete any stray `review-*.json` at the repo root (safety net).

## What this skill does not do

- It does not merge the PR. Merging is a separate, explicit decision.
- It does not create the PR — that already happened in `workflow-handoff`.
- It does not fix anything without going through the walkthrough's disposition tracking — every change is recorded, not silent.
