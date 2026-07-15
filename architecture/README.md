# Architecture

This directory is the **sole source of truth** for Event Ledger's system
design. It is written to be read by both humans and the
`architecture-advisor` agent (see
[.claude/agents/architecture-advisor.md](../.claude/agents/architecture-advisor.md)),
which has no inline rules of its own — everything it knows about this
system's design comes from reading these files in full. If a design
decision isn't written here, it doesn't exist as far as that agent (or
anyone onboarding cold) is concerned.

## Reading order

Start with [vertical-architecture.md](vertical-architecture.md) — it
defines system topology and the decisions every other document assumes
(confirm-before-persist, DB-level idempotency, balance-on-read). Every
other document links back to it instead of restating those decisions.

| Document | Covers |
|---|---|
| [vertical-architecture.md](vertical-architecture.md) | Topology, service boundaries at a system level, the three core cross-cutting decisions |
| [gateway-architecture.md](gateway-architecture.md) | Event Gateway internals and request flow |
| [account-architecture.md](account-architecture.md) | Account Service internals and request flow |
| [data-model.md](data-model.md) | SQLite/EF Core schema, constraints, indexes |
| [observability.md](observability.md) | Tracing, structured logging, health checks, metrics |
| [resiliency.md](resiliency.md) | Circuit breaker + timeout pipeline, graceful degradation |
| [deployment-architecture.md](deployment-architecture.md) | Docker Compose and manual local run topology |

Related, but outside this directory:

- [../standards/](../standards/) — cross-cutting conventions (API
  contracts, event payload shape, naming, logging setup, service
  boundaries, and the backend project scaffold) that apply *within* the
  architecture defined here, rather than defining the architecture itself.
- [../governance/architecture-docs-edit-gate.md](../governance/architecture-docs-edit-gate.md)
  — why this directory is treated as high-leverage and how edits to it
  should be handled.

## Editing rules

1. **State each decision once, in the document that owns it.** If you find
   yourself re-explaining *why* balance is computed on read, or *why*
   there's no outbox, you're duplicating
   [vertical-architecture.md](vertical-architecture.md) — link to it
   instead.
2. **Every document in this directory ends with an "Anti-patterns to
   avoid" section.** These are quoted **verbatim** (not paraphrased) by the
   `architecture-advisor` agent when flagging a proposed change that
   conflicts with recorded design decisions. Write each bullet as a
   complete, self-contained statement — it needs to make sense quoted in
   isolation, without the surrounding prose.
3. **A change to a decision here is a change to the decision, not an
   addition next to it.** If a documented decision needs to change, edit
   the document in place (and update anything that links to it) rather
   than layering a newer, contradictory decision on top and leaving both.
   See [../governance/architecture-docs-edit-gate.md](../governance/architecture-docs-edit-gate.md)
   for how these edits should be reviewed.
4. **New architecture documents get linked from the table above and from
   [vertical-architecture.md](vertical-architecture.md)'s cross-cutting
   table.** An architecture document that nothing links to is effectively
   invisible to both readers and the advisor agent.
5. **Don't introduce a pattern here to match a larger reference
   system's conventions.** Every decision in this directory is justified
   against this system's actual constraints (2 services, solo contributor,
   local/offline) — see
   [vertical-architecture.md](vertical-architecture.md#system-shape). If a
   pattern's justification is "this is how it's normally done," that's a
   sign it doesn't belong here.
