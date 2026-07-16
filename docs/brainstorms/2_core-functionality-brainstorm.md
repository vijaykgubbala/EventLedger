# Brainstorm: Core Functionality (idempotency, out-of-order tolerance, balance computation, validation) — v2

**Date:** 2026-07-16
**Issue:** #2

> **Note:** this supersedes an earlier brainstorm for the same issue,
> written before issue #3 (Service separation) had real delivered code —
> at that point #3 was only a plan. Redone from scratch at the user's
> explicit request, now grounded in issue #3's *actual* merged-pending
> code (PR #11) rather than its plan. Where nothing has changed, this
> version says so plainly rather than padding with restated reasoning.

## Problem Statement

Implement the Gateway's `POST /events` and the Account Service's
`POST /accounts/{accountId}/transactions` so that: a duplicate `eventId`
never creates a second row or double-applies a transaction; events
applied out of arrival order still produce a correct balance and a
correctly-ordered `GET /events?account=...` listing; balance is always
`SUM(CREDIT) − SUM(DEBIT)`; and invalid payloads are rejected with `400`
before any persistence or network call. This is
[issue #2](https://github.com/vijaykgubbala/EventLedger/issues/2),
milestone "Phase 1 - Core Domain & Service Separation."

## Codebase Context

### What's unchanged since the first brainstorm

Every architecture/standards decision below was re-verified against the
current repo state and is **identical** to the first pass — nothing has
changed here, so this is confirmation, not new reasoning:

- Idempotency via DB `UNIQUE` constraint, never check-then-insert; a
  duplicate returns the original record with `200`/`201`, never `409` —
  [architecture/vertical-architecture.md](../../architecture/vertical-architecture.md).
- Gateway's `POST /events` is two-stage: fast-path `SELECT` (optimization),
  then `UNIQUE`-constraint-enforced insert-or-fetch after the Account
  Service confirms (the real guarantee) —
  [architecture/gateway-architecture.md](../../architecture/gateway-architecture.md).
- Account Service accepts only `eventId`/`type`/`amount` from the Gateway,
  enforces its own `UNIQUE` constraint plus a `CHECK`-constraint backstop
  — [architecture/account-architecture.md](../../architecture/account-architecture.md).
- Exact `events`/`transactions` schema, balance SQL —
  [architecture/data-model.md](../../architecture/data-model.md).
- Status codes, error shape, validation rules —
  [standards/api.md](../../standards/api.md),
  [standards/events.md](../../standards/events.md).
- Insert-or-fetch code pattern, verbatim —
  [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md).
- Test checklist and the `InMemory`-is-`critical`-if-found rule —
  [.claude/agents/review-testing.md](../../.claude/agents/review-testing.md).

### What's new since the first brainstorm

Issue #3 is now **implemented, reviewed, and has an open PR** ([#11](https://github.com/vijaykgubbala/EventLedger/pull/11))
on branch `3_service-separation` — not yet merged to `master`, so this
plan still executes *after* that merge, same dependency as before. But
unlike the first brainstorm, the actual delivered shape is now known,
not just planned:

- `Application/` and `Domain/` folders **do not exist yet** in the
  delivered scaffold — only `Controllers/`, `Infrastructure/`,
  `Middleware/` exist per service. This story creates them, as expected —
  no surprise here, just confirming the folders this plan will populate
  aren't already there in some unexpected shape.
- The orchestrator extension methods are real code now, not a
  description:

  ```csharp
  // src/EventLedger.Gateway/Infrastructure/ServiceCollectionExtensions.cs
  public static WebApplicationBuilder AddGatewayInfrastructure(this WebApplicationBuilder builder)
  {
      builder.Host.UseSerilog();
      builder.Services.AddControllers();

      return builder;
  }
  ```

  `Program.cs` calls this exactly once (`builder.AddGatewayInfrastructure();`)
  inside a `try/catch/finally` with `Log.Fatal`/`Log.CloseAndFlush`. This
  story's DbContext/repository registration has a real, concrete place to
  land now — see Q&A below.
- `AppMarker.cs` (`public partial class Program { }`) already exists in
  both services — the `WebApplicationFactory<Program>` gotcha this
  story's Phase 5/6 integration tests would otherwise need to worry about
  is already closed. Nothing to do here, just noting it's a non-issue now.
- Both test projects already carry
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  (added in #3 because `Console.Out` redirection and static `Log.Logger`
  are global state). This story's new tests inherit that setting
  automatically — real SQLite temp files per test class don't have the
  same global-state race, but the assembly-level setting applies
  regardless, so no new configuration is needed either way.

## Q&A Decisions

**Q1 (new this pass): Where should EF Core `DbContext` registration go — inside the existing `AddGatewayInfrastructure()` method body, or as a new, separately-chained extension method?**
A: Not answered — deferred to Plan phase. Defaulting to **extend the existing method in place**, since that's the design intent issue #3's own plan explicitly recorded ("the one call site each story adds a line to, so `Program.cs`'s shape never needs to change again") — this isn't really an open design question so much as a reconfirmation of an already-committed decision, now that the method's real signature is known.

**Q2 (new this pass): Should `SubmitEventHandler`/`ApplyTransactionHandler`/`EventValidator` be explicitly registered in the DI container, or constructed directly with no registration?**
A: Not answered — deferred to Plan phase. Defaulting to **explicit `AddScoped<T>()` registration**, matching standard ASP.NET Core practice and keeping dependencies visible/swappable via `WebApplicationFactory`'s service-override hooks in tests, consistent with how `Controllers/`, `Infrastructure/`, `Middleware/` are already wired via the DI container rather than manual construction.

**Q3–Q6 (from the first brainstorm, reconfirmed, not re-asked):** sequencing (plan/execute after #3 merges — now sharper: after PR #11 merges specifically, not just "after #3 is done"), test DB lifecycle (fresh temp SQLite file per test class), duplicate-with-different-payload logging (compare and log a `Warning`, still return the original), backstop `CHECK`-violation status code (`500` + `Error` log). All still apply unchanged — see the prior brainstorm's git history if the specific reasoning is needed; not restated here since nothing about them changed.

## Proposed Approaches

Unchanged from the first pass — nothing in issue #3's actual delivered
code invalidates this reasoning, it only sharpens *where* the code lands.

### Approach 1: Repository interfaces between `Application/` and `Infrastructure/`

Same as before: rejected. Idempotency correctness requires real SQLite
regardless of a repository abstraction, so the "swap in a fake for fast
tests" argument doesn't hold, and `standards/backend-architecture.md`'s
own anti-patterns rule out a single-implementation interface with no
test-double reason to exist.

### Approach 2: `Application/` handlers use `DbContext` directly, registered via DI

Same as before, now with a concrete landing spot: handlers registered in
`AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()` per Q1,
constructor-injected per Q2. No repository interface, no shared code
between services.

## Recommendation

**Approach 2, unchanged.** The first brainstorm's reasoning holds fully:
Approach 1's testability argument doesn't survive contact with the
`InMemory`-is-banned rule, and Approach 2 now has an even more concrete
integration point than before — a real `AddGatewayInfrastructure()`
method with a known current body, not a documented convention waiting to
be built.

## Related Docs

- [docs/plans/2_core-functionality-plan.md](../plans/2_core-functionality-plan.md) — the existing plan from the first brainstorm pass; **should be regenerated** via `workflow-plan 2` to incorporate this brainstorm's Q1/Q2 findings (the real `AddGatewayInfrastructure()` shape wasn't known when that plan was written).
- [architecture/gateway-architecture.md](../../architecture/gateway-architecture.md) — the two-stage flow this story implements.
- [docs/patterns/2026-07-15-idempotency-key-race.md](../patterns/2026-07-15-idempotency-key-race.md) — the insert-or-fetch pattern.
- PR [#11](https://github.com/vijaykgubbala/EventLedger/pull/11) — issue #3's delivered scaffold, the concrete foundation this story builds on.
- Next: `workflow-plan 2` (regenerate, not reuse the existing plan as-is).
