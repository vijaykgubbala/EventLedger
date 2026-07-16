---
name: workflow-plan
description: Create a research-backed implementation plan with checkboxes for a GitHub issue. Use when the user has a feature or fix to implement and wants a detailed plan before writing code.
disable-model-invocation: true
argument-hint: "[issue-number | topic description]"
---

# Plan

You are creating a detailed, research-backed implementation plan. Your
job is to gather context from the codebase and past solutions, then
produce a plan that an engineer (or `workflow-execute`) can follow step
by step. Scaled down from a multi-repo/ADO-backed reference: no
`DOCS_ROOT` routing (single repo, everything under `docs/`), and issue
tracking is GitHub Issues, not a rich work-item state machine.

## Step 0: Resolve Input and Check the Issue

1. If `$ARGUMENTS` is a bare number or `#N`, resolve it: `gh issue view <N> --json number,title,body,state,labels,assignees,milestone,url`.
2. **If an issue was resolved:**
   - If `state == "CLOSED"`, warn via `AskUserQuestion`: "Issue #`<N>` is closed. Continue planning anyway?" — options: "Continue anyway", "Stop".
   - If `assignees` is empty, run `gh issue comment <N> --body "Plan started — assigning to current user."` then `gh issue edit <N> --add-assignee "@me"`. Planning does not close the issue or change its milestone.
3. **If no issue was resolved**, treat `$ARGUMENTS` as a free-text topic and continue to Step 1.

## Step 1: Check for a Prior Brainstorm

Search `docs/brainstorms/` for any existing brainstorm related to the topic in `$ARGUMENTS` (by issue number in the doc's `**Issue:**` line, or by topic slug). Use Glob to find matching files. If a relevant brainstorm exists, read it and use its recommended approach, Q&A decisions, and codebase context as input to the plan.

## Step 2: Research

1. Dispatch a Task (`subagent_type: Explore`) to understand codebase patterns, conventions, and existing code relevant to the feature — point it at `architecture/`, `standards/`, `docs/patterns/`, and (once code exists) `src/` and `tests/`. Also ask it to identify the project's test framework/conventions once `tests/` exists (xUnit, per [standards/backend-architecture.md](../../../standards/backend-architecture.md#test-project-layout)).
2. Search `docs/solutions/` for prior lessons relevant to this topic (may not exist yet on early stories — that's fine, note "no relevant prior solutions found" rather than treating an empty directory as an error).

**Architecture pre-flight (mandatory whenever the plan touches `src/`):**

Invoke the `architecture-guide` skill with a one-sentence description of what this plan implements and which service(s)/layer(s) it touches. Extract the returned guidance — which folder (`Controllers/Application/Domain/Infrastructure/Middleware`, per [standards/backend-architecture.md](../../../standards/backend-architecture.md)) each new type belongs in, and any conflict with a documented decision. Incorporate these rules directly into the plan's implementation steps; if a rule conflicts with the brainstorm's recommendation, surface the conflict to the user before writing the plan.

## Step 3: Identify and Resolve Risks

Before writing the plan, review all research output and identify every risk, unknown, ambiguity, or decision point. This includes:
- Multiple valid implementation approaches where the choice affects architecture
- Ambiguous requirements not resolved by the issue body or brainstorm — cross-check against the ambiguities already catalogued for this project if relevant (idempotency-on-mismatched-payload, the Gateway balance-endpoint gap, etc.)
- If the plan adds or modifies any async method in the call chain from a controller action down to a repository/EF Core call or an outbound `HttpClient` call, check that every step threads a `CancellationToken` parameter through to the next call rather than dropping it after the first `await` — see [docs/patterns/2026-07-15-cancellation-token-propagation.md](../../../docs/patterns/2026-07-15-cancellation-token-propagation.md). Note this explicitly as an implementation-step detail rather than a question to ask the user; there's no real alternative worth presenting as a choice.

If none exist, proceed to Step 4.

If risks exist, use `AskUserQuestion` to present them — one question per distinct decision, 2–4 labeled options. Do not proceed to Step 4 until all questions are answered. Summarize each decision in a short "Decisions" record to carry into the plan.

## Step 4: Synthesize and Write the Plan

Create `docs/plans/` if it does not exist yet.

Write the plan to `docs/plans/<issue-id>_<name>-plan.md` when an issue
was resolved in Step 0 (`<issue-id>` bare, e.g. `2`) — matching
`workflow-brainstorm`'s convention, so a plan and its brainstorm share
the same issue-anchored prefix and sort together. `<name>` is a short
slug. For a free-text topic with no issue, fall back to
`docs/plans/YYYY-MM-DD-<type>-<name>-plan.md` (`<type>` from `feature`,
`fix`, `refactor`, `infra`, `docs`), since there's no issue number to
anchor to.

### Document structure

```markdown
# <Plan Title>

**Issue:** #<N> (or "none — free-text topic")

## Context

[Background on what this plan addresses. Reference the brainstorm document if one exists.]

## Relevant Learnings

[Summarize applicable findings from docs/solutions/ and docs/patterns/. If none found, say so — this is expected on early stories.]

## Implementation Steps

[Break the work into logical, ordered steps. Each step is a checkbox item. Group related steps under subheadings if the plan is large.]

### <Phase or Section Name>

- [ ] Step description (specific enough to act on, referencing exact file paths per standards/backend-architecture.md)
- [ ] Step description
  - Details, file paths, or notes relevant to this step

### <Next Phase>

- [ ] ...

## Testing Strategy

### Test Environment

xUnit, per [standards/backend-architecture.md](../../../standards/backend-architecture.md#test-project-layout). Real file-based SQLite for anything touching idempotency — never `InMemory`, per [docs/patterns/2026-07-15-idempotency-key-race.md](../../../docs/patterns/2026-07-15-idempotency-key-race.md).

### Test Cases

For each implementation phase, list test cases **before** the implementation steps they verify:

- **Description**: What the test asserts and the expected behavior
- **Type**: Unit or integration (per this project's two test tiers — see [standards/backend-architecture.md](../../../standards/backend-architecture.md#test-project-layout))
- **Edge cases**: Boundary conditions or failure modes to cover
- **Phase reference**: Which implementation phase/step this test covers

Example:
- [ ] Test: duplicate `eventId` submission returns the original record with `200`, not a new row (integration, Phase 1 Step 2)
- [ ] Implement: idempotent insert-or-fetch in `EventRepository`

## Decisions Made

[Record of key decisions made during planning, with rationale. No open questions. Omit this section if no decisions were required.]

### Known Constraints

[Unavoidable external factors accepted as-is. Omit if none apply.]
```

### Guidelines for writing steps

- Each checkbox item should be a concrete, actionable task
- Reference specific files, functions, or patterns from the codebase research
- Order steps so earlier steps do not depend on later ones
- Every implementation step should reference which test case(s) it satisfies
- Test-writing steps (`- [ ] Write test: ...`) must precede their corresponding implementation steps in the checkbox order

## Step 5: Suggest Next Step

Tell the user the plan has been written and suggest running `workflow-execute` (e.g. `/workflow-execute <N>`) to begin implementation.

## Step 6: Update the Issue (if applicable)

If an issue was resolved in Step 0:

```
gh issue comment <N> --body "Plan created. Document: docs/plans/<filename>. Phases: <N>. Next step: /workflow-execute <N>."
```

## Constraints

- Do not write or modify any application code. This skill produces documentation only.
- Do not skip the research step. Plans must be grounded in codebase context and past learnings, not invented from scratch.
- Do not write the plan until all identified risks have a user-confirmed answer.
- Never list an unresolved risk or open question in the final plan document.
