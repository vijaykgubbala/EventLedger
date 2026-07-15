# Event Ledger

## Project brief

Event Ledger is a take-home implementation of a 2-service financial
transaction system: a public-facing **Event Gateway** and an internal
**Account Service**, communicating over synchronous REST. The system must
tolerate out-of-order and duplicate event delivery, and degrade gracefully
when the Account Service is unavailable. Full requirements are sourced from
[docs/Assignment - Senior Software Engineer - AI.docx](docs/Assignment%20-%20Senior%20Software%20Engineer%20-%20AI.docx)
— that document is the sole source of truth for *what* is required; this
repository's `architecture/` and `standards/` directories are the source of
truth for *how* it's built.

Tech stack: ASP.NET Core (.NET 8), C#, SQLite via EF Core, OpenTelemetry,
Serilog, Polly v8. See [README.md](README.md) for the full architecture
overview and [architecture/](architecture/) for the detailed design
decisions and their rationale.

This is a **2-service, solo-contributor, local/offline system** — no cloud
infrastructure, no frontend, no multi-tenancy. Every doc and tool in this
repo is scaled to that reality; see
[architecture/vertical-architecture.md](architecture/vertical-architecture.md#system-shape).

## Scaffold status

This repository currently contains **documentation and Claude Code tooling
only** — no `.sln`, no `.csproj`, no application code. That's deliberate:
architecture, governance, and standards were written first so that the
follow-up code pass has a settled design to implement against rather than
inventing decisions inline. The concrete project scaffold the code pass
should create is specified in
[standards/backend-architecture.md](standards/backend-architecture.md).

Read [architecture/vertical-architecture.md](architecture/vertical-architecture.md)
first — every other document links back to it instead of restating its
decisions.

## Working in this repo

- **Before implementing anything that touches a documented design
  decision**, use the `architecture-guide` skill to check the proposed
  change against `architecture/` first — see
  [governance/architecture-docs-edit-gate.md](governance/architecture-docs-edit-gate.md)
  for why that directory is treated as high-leverage.
- **When a design decision changes**, update the owning document in
  `architecture/` in the same change — don't let code and docs drift.
- **Lessons learned from working in this codebase** go in
  [docs/patterns/](docs/patterns/), dated and frontmattered, linked back to
  the architecture doc they relate to.

## Skill pointers

| Skill | Use for |
|---|---|
| [`architecture-guide`](.claude/skills/architecture-guide/SKILL.md) | Checking a proposed change against `architecture/` before implementing it |
| [`commit`](.claude/skills/commit/SKILL.md) | Creating a git commit via the `committer` agent (named-file staging, secret-scan, no `--amend` after a hook failure) |
| [`test-dotnet`](.claude/skills/test-dotnet/SKILL.md) | Running `dotnet test` with coverage once the code pass exists, flagging under-80% coverage |
| [`workflow-review`](.claude/skills/workflow-review/SKILL.md) | Parallel-dispatching all `review-*` agents over a change and walking through merged findings |

## Agent pointers

Review agents (`review-correctness`, `review-dotnet`, `review-testing`,
`review-security`, `review-maintainability`) are read-only and produce JSON
findings with severities `critical | warning | suggestion` — see
[.claude/agents/](.claude/agents/) for each one's narrow trigger scope.
`architecture-advisor` is also read-only and refuses to run without
`architecture/` present. `committer` is the only agent with write access to
git state, and only to staging/committing named files.
