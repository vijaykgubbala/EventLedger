# Architecture Docs Edit Gate

## Why `architecture/` is high-leverage

`architecture/` is the **sole authority** consulted by the
`architecture-advisor` agent (see
[.claude/agents/architecture-advisor.md](../.claude/agents/architecture-advisor.md)).
That agent carries no inline rules — it refuses to run at all if
`architecture/` doesn't exist, and every judgment it makes about whether a
proposed change fits this system's design comes from reading those files in
full at invocation time. That makes `architecture/` different from every
other doc in this repo: a wrong or stale sentence there doesn't just mislead
a human reader who might catch it — it becomes the advisor agent's ground
truth, silently, for every future review it does.

Concretely: if `resiliency.md` still described a bare retry after the
design moved to circuit-breaker-primary, the advisor agent would approve a
PR that reintroduced a bare-retry-forever call, not because it's a bad
agent, but because that PR would be genuinely *consistent* with what the
document told it. The leverage runs in the same direction as the risk — a
correct architecture doc makes every future review cheap and accurate; a
stale one makes every future review confidently wrong.

## What this means in practice, for a solo-repo

This is a one-contributor, local-only repository — there is no code owner
review, no required-approvers branch policy, and setting one up would be
process theater for an audience of one. The enforcement this document
describes is **self-discipline backed by tooling**, not a gate anyone else
operates:

- **Before merging a change that alters system behavior described in
  `architecture/`, update the relevant document in the same change.** Not
  "file a follow-up," not "update it later" — architecture and the code
  that implements it move together, or the document starts lying
  immediately.
- **Run the `architecture-guide` skill** (see
  [.claude/skills/architecture-guide/SKILL.md](../.claude/skills/architecture-guide/SKILL.md))
  before implementing a change that touches a documented decision — it
  dispatches to the `architecture-advisor` agent to check the proposed
  change against current docs *before* code is written, which is cheaper
  than discovering a conflict after.
- **Treat every `architecture/*.md` edit as deliberate**, per the editing
  rules in [architecture/README.md](../architecture/README.md#editing-rules)
  — replace a superseded decision in place, don't leave two contradictory
  decisions coexisting because the old paragraph never got deleted.

## Scope

This gate applies specifically to `architecture/`, because that's the
directory the advisor agent treats as authoritative. `standards/` and
`docs/patterns/` are also load-bearing documentation, but they don't carry
the same "silently becomes an AI agent's only source of truth" property —
mistakes there mislead a human reader, who can apply judgment; a mistake in
`architecture/` misleads an agent that has been explicitly told to trust it
completely. That asymmetry is why this directory gets a named gate and the
others don't.
