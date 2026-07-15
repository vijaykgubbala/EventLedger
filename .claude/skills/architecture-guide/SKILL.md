---
name: architecture-guide
description: Check a proposed change against the architecture/ directory before implementing it. Use before writing code that touches idempotency, service boundaries, persistence, tracing, resiliency, or deployment topology.
allowed-tools: Task
---

This skill is a thin dispatcher. It does not read `architecture/` itself
and does not make judgments — it hands the proposed change to the
`architecture-advisor` agent, which owns that responsibility (see
[.claude/agents/architecture-advisor.md](../../agents/architecture-advisor.md)).

## Steps

1. Take the user's description of the change they're about to make (or a
   diff/plan if one exists in the conversation).
2. Dispatch a `Task` to the `architecture-advisor` agent, passing along:
   - A concise description of the proposed change.
   - Any relevant file paths or diff content already in context.
3. Return the agent's findings to the user as-is — do not summarize away
   the quoted anti-pattern bullets or the file citations; those are the
   point of the agent's output.

Do not attempt to resolve a flagged conflict yourself within this skill.
If the advisor flags a conflict, surface it and let the user decide how to
proceed (adjust the plan, or — if the architecture decision itself is what
should change — edit the owning `architecture/` document per
[governance/architecture-docs-edit-gate.md](../../../governance/architecture-docs-edit-gate.md)).
