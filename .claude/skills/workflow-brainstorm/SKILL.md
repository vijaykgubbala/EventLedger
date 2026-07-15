---
name: workflow-brainstorm
description: Facilitate structured brainstorming for a GitHub issue or a free-text problem statement. Use when the user wants to explore what to build before writing a plan.
disable-model-invocation: true
argument-hint: "[issue-number | topic description]"
---

# Brainstorm

You are facilitating a structured brainstorming session. Your job is to
understand the problem space, gather context from the codebase and the
user, and propose concrete approaches. Adapted from a larger
multi-repo/Azure-DevOps-backed reference workflow — this version is
scaled down for a single-repo project tracked with GitHub Issues; there
is no `DOCS_ROOT` routing (everything lives under `docs/`, per
[docs/CLAUDE.md](../../../docs/CLAUDE.md)).

## Step 0: Resolve Input and Check the Issue

1. If `$ARGUMENTS` is a bare number or `#N`, resolve it: `gh issue view <N> --json number,title,body,state,labels,assignees,milestone,url`.
   - **If found:** this is issue-scoped brainstorming. Note the title, body, and milestone for context.
   - **If `gh` errors** (no such issue): fall through and treat `$ARGUMENTS` as a free-text topic.
2. **If an issue was resolved:**
   - If `state == "CLOSED"`, warn via `AskUserQuestion`: "Issue #`<N>` is closed. Continue brainstorming anyway?" — options: "Continue anyway", "Stop".
   - If `assignees` is empty, run `gh issue comment <N> --body "Brainstorm started — assigning to current user."` then `gh issue edit <N> --add-assignee "@me"`. Brainstorming does not change the issue's open/closed state or its milestone.
3. **If no issue was resolved**, treat `$ARGUMENTS` as a free-text topic and continue to Step 1.

## Step 1: Research the Codebase

Dispatch a Task (`subagent_type: Explore`) to find patterns, conventions, and existing code related to the topic in `$ARGUMENTS`. Point it at `architecture/`, `standards/`, and (once application code exists) `src/` — the goal is to ground the brainstorm in what's actually here, not invent a design in a vacuum.

## Step 2: Ask Clarifying Questions

Ask clarifying questions **one at a time** using `AskUserQuestion`. Use multiple choice format with 2 to 4 options per question. Each question should:

- Address a single decision point or constraint
- Offer concrete options grounded in what the codebase research revealed
- Be written in plain language

Ask between 3 and 7 questions depending on the complexity of the topic. Stop when you have enough context to propose meaningful approaches. After each answer, consider whether the response raises a follow-up before moving to the next topic.

### What to ask about

- **Problem scope**: What exactly are we solving? What is out of scope?
- **Constraints**: Does this touch a decision already recorded in `architecture/`? (Check first — see [architecture-guide](../architecture-guide/SKILL.md).)
- **Existing patterns**: The codebase already does X. Should we follow that pattern or diverge?
- **Dependencies**: Does this touch the Gateway→Account Service boundary, or stay within one service?
- **Priority**: If we had to cut scope for the 4-hour budget, what is essential vs. nice-to-have?

## Step 3: Propose Approaches

Based on the codebase research and the user's answers, propose **2 to 3 approaches** with pros and cons for each. Apply YAGNI: favor the simplest approach that solves the stated problem — see [architecture/vertical-architecture.md](../../../architecture/vertical-architecture.md#system-shape) for why this project treats that as a hard constraint, not a preference. Clearly recommend one approach and explain why.

## Step 4: Write the Brainstorm Document

Before writing, run the docs bootstrap: if `docs/CLAUDE.md` does not exist, this has already gone wrong — stop and tell the user (it should exist; see [docs/CLAUDE.md](../../../docs/CLAUDE.md)). Create `docs/brainstorms/` if it does not exist yet.

Write the brainstorm to `docs/brainstorms/YYYY-MM-DD-<topic-slug>-brainstorm.md` using today's date and a short slug derived from the topic.

### Document structure

```markdown
# Brainstorm: <Topic>

**Date:** YYYY-MM-DD
**Issue:** #<N> (or "none — free-text topic")

## Problem Statement

[Concise description of the feature idea or problem from $ARGUMENTS]

## Codebase Context

[Summary of relevant findings from the research: existing patterns, related code, conventions, and any owning architecture/standards doc]

## Q&A Decisions

[For each question asked:]

**Q[N]: [Question]**
A: [User's answer]

## Proposed Approaches

### Approach 1: [Name]

[Description]

**Pros:**
- ...

**Cons:**
- ...

### Approach 2: [Name]

[Description]

**Pros:**
- ...

**Cons:**
- ...

## Recommendation

[Which approach and why. Reference Q&A decisions that support this choice.]

## Related Docs

[Links to related plans, solutions, or architecture/standards docs if they exist for this topic]
```

## Step 5: Suggest Next Step

Tell the user the brainstorm document has been written and suggest running `workflow-plan` (e.g. `/workflow-plan <N>`) to create an implementation plan from the recommended approach.

## Step 6: Update the Issue (if applicable)

If an issue was resolved in Step 0:

```
gh issue comment <N> --body "Brainstorm completed. Document: docs/brainstorms/<filename>. Recommended approach: <name>. Next step: /workflow-plan <N>."
```

## Constraints

- Do not write or modify any application code. This skill produces documentation only.
- Do not skip the codebase research step. Questions must be grounded in the actual code.
- Do not invent a GitHub Issues state machine (there is none — issues are open or closed, full stop). Don't try to mirror a richer status ladder than GitHub actually has.
