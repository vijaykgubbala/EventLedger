---
name: workflow-compound
description: Document solved problems and decisions as searchable institutional knowledge under docs/solutions/ and docs/patterns/. Use when the user has fixed a bug, resolved an issue, completed a story, or wants to capture decisions for future reference.
disable-model-invocation: true
argument-hint: "[context]"
---

# Compound: Encode Knowledge Into the Repo

Capture engineering knowledge — solved problems, decisions, rationale,
patterns — so future `workflow-plan` runs discover and learn from it.
Scaled down from a reference version built for a large multi-repo hub
(5 parallel source-mining subagents, a full/context mode split with
separate files per mode): for a ~10-issue, 4-hour project, one mode with
two lightweight subagents is enough. Don't reintroduce the larger
version's ceremony just because it exists upstream.

## Parse Mode

| Argument | Behavior |
|---|---|
| `context` | Fast mode. Capture only from the current conversation. Skip Step 2's subagents; synthesize inline from what's already in context. |
| *(none)* | Full sweep. Run both subagents below. |

## Step 1: Gather Context

Get the current branch name and extract the issue number (leading digit sequence, e.g. `feat/6-resiliency` → `6`).

## Step 2: Launch Subagents (full mode only)

Launch both in parallel using the Task tool. Subagents must NOT write files — they return structured text for you to assemble in Step 3.

### Git Archaeologist (subagent_type: general-purpose)

1. `git log --oneline -20`, `git diff master...HEAD --stat`, `git diff master...HEAD`.
2. Read commit messages for intent and reasoning.
3. Extract: what changed and why, any structural shifts, patterns in the changes.
4. Return structured text, not raw diff — keep the patch out of main context.

### Docs Scout (subagent_type: general-purpose)

1. Search `docs/solutions/` for files related to this work by topic or file path.
2. Search `docs/patterns/` for related pattern files (already seeded with 2 entries from the initial scaffold).
3. List existing category subdirectories under `docs/solutions/`.
4. Determine the best-fit category (descriptive kebab-case) or propose a new one.
5. Generate a short kebab-case slug from the primary topic.
6. Return: related file paths with one-line descriptions, proposed category, proposed slug.

## Step 3: Assemble and Write

1. Review the subagent outputs (or, in context mode, the current conversation) together. Identify distinct pieces of knowledge: solved problems, decisions, patterns discovered.
2. Merge overlapping content — if both sources describe the same insight, combine into one entry.
3. Decide how many solution files to write. One topic per file: different root causes or independent fixes get separate files. Don't ask the user — decide.
4. Assess whether any insight is a **general pattern** (reusable across future stories, not specific to this one) vs. a **solution** (one specific problem/fix). Idempotency and cancellation-token gotchas are patterns; "the balance endpoint was missing from the Gateway table" is a solution.

### Write solution files

For each solution:

```markdown
---
title: "<Title>"
date: YYYY-MM-DD
category: <category>
related: [<related solution or pattern paths>]
---

# <Title>

## Symptoms
<What was observed that indicated something was wrong>

## Root Cause
<The underlying issue>

## Solution
<What was done, with enough detail to reproduce>

## Key Insight
<The one-sentence takeaway>

## Prevention
<How to avoid this in future stories, if applicable>
```

Write to `docs/solutions/<category>/<slug>-<YYYY-MM-DD>.md`. Create the category directory if needed. Cross-reference: search `docs/brainstorms/`, `docs/plans/`, `docs/handoffs/`, `docs/reviews/` for files mentioning the same branch or issue number; add a `## Related Docs` section linking any matches.

### Write pattern files

For each pattern identified, write to `docs/patterns/YYYY-MM-DD-<descriptive-slug>.md` — same structure and header discipline as the two existing seed files
([2026-07-15-idempotency-key-race.md](../../../docs/patterns/2026-07-15-idempotency-key-race.md),
[2026-07-15-cancellation-token-propagation.md](../../../docs/patterns/2026-07-15-cancellation-token-propagation.md)):

```markdown
---
title: <Pattern Title>
date: YYYY-MM-DD
related: [<related solution or pattern paths>]
---

# <Pattern Title>

## Pattern
<What this pattern is and when it applies>

## Guidance
<Actionable advice for applying it>

## Examples
<Concrete examples or references to solutions where this pattern was observed>
```

## Step 4: Update the Issue

If a digit sequence was found in the branch name:

```
gh issue comment <N> --body "Solution documented. File: docs/solutions/<category>/<slug>. Category: <category>."
```

If no issue number was found, skip this step silently — don't ask the user to invent one for a docs-capture pass.

## Constraints

- Do not modify application code. This skill writes only to `docs/solutions/` and `docs/patterns/`.
- Keep solution files concise and actionable — scan, don't read.
- Plain language. No jargon a reader new to this repo wouldn't know.
- Deduplicate overlapping content across sources before writing. Err toward fewer, more focused files over one file per minor observation.
