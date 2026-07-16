---
issue: 21
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/21
branch: 21_verification-guide
base: master
plan: docs/plans/21_verification-guide-plan.md
---

# Handoff: Story 10 — Deployment & Requirements Verification Guide

## Release Notes

This story adds `docs/verification-guide.md` — a companion to README for
an evaluator who wants to exercise the running system directly rather
than only reading source code or test files. It's organized around the
assignment's own numbered requirements (Core Functionality, Service
Separation, Distributed Tracing, Observability, Resiliency, Graceful
Degradation), and every single command in it was actually run against a
live `docker compose` deployment before being written down — the
response bodies shown are transcribed from real output, not predicted
from reading the source.

That discipline surfaced two things worth knowing about even outside
this guide. First, the circuit-breaker exercise: the architecture doc's
configured "4-call minimum throughput" figure describes concurrent calls
landing in the same sampling window, not sequential retries — because
each bounded failed call takes ~6.7–6.8s (close to the 10s window
itself), firing requests one at a time never trips the breaker no matter
how many you fire, only a burst of concurrent ones does. Live testing
also showed this is itself timing-sensitive (one run tripped the circuit
on the first burst of 10 concurrent requests, another needed a second
burst), so the guide describes it honestly rather than asserting a fixed
reliable count. This is now captured as a durable pattern —
`docs/patterns/2026-07-16-circuit-breaker-sequential-failures-dont-trip-breaker.md`
— since it's a real emergent property of the deployed system, not just a
testing quirk.

Second: the Resiliency and Graceful Degradation sections originally each
did their own separate Account-Service stop/restart cycle. A `/simplify`
pass caught that Graceful Degradation's checks don't need the service to
be freshly re-broken — it only needs to still be down, which is already
true at the end of the Resiliency section's own outage. The two sections
now share one outage window, proving all six behaviors (bounded failure,
circuit trip, circuit open, nothing-persisted, unaffected local reads,
degraded balance passthrough, recovery) with a single stop and a single
start instead of two round trips.

Everything else in the guide is a straightforward, cross-referenced
walkthrough: a quick-reference cheat sheet, then per-requirement
sections that link out to README and the relevant `architecture/*.md`
doc rather than restating either.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| `docs/verification-guide.md` (new file) | None — pure documentation, no production code touched | Whether every command and response body shown is genuinely accurate, not just plausible-looking | Every command was run live against a real `docker compose` deployment during authoring, including a full re-verification pass after the `/simplify` restructuring; the full solution test suite (94 tests) was re-run after every content phase to confirm no regression was introduced elsewhere |
| `README.md` (+5 lines) | Trivial — one cross-reference link added, no existing content changed | Whether the link duplicates anything already in README | Placed immediately after the existing "Running the tests" coverage table, framed explicitly as an alternative (hands-on) path to the same proof, not a restatement |
| `docs/patterns/2026-07-16-circuit-breaker-sequential-failures-dont-trip-breaker.md` (new pattern) | None — documentation only | Whether the described behavior is accurately characterized, since it's a nuance not previously documented anywhere | Directly observed via `time curl` against a live outage on two separate live runs, both showing the same qualitative pattern (bounded sequential calls never trip the breaker; concurrent bursts do, with some run-to-run timing variance) |

## Test Coverage

### Planned vs Actual

This story adds no C# code, so "testing" is the live verification
discipline described above rather than `dotnet test` coverage. The
plan's own Test Cases section (manual, live) maps directly to the
guide's six numbered sections:

| Planned Test | Status | Notes |
|---|---|---|
| Every cheat-sheet one-liner returns the documented status/shape when actually run | verified | Confirmed live during Phase 2 |
| Idempotency, out-of-order, balance (incl. zero-transaction), and all 6 validation-rejection examples produce exactly the documented response bodies | verified | Confirmed live during Phase 3, using one continuous account narrative |
| The trace-ID grep command finds a matching `TraceId` in both services' logs for one real request | verified | Confirmed live during Phase 4 — also corrected an incorrect assumption from the brainstorm (Serilog's `JsonFormatter` puts `TraceId` at the top level, not only nested under `Properties`) |
| The circuit-breaker exercise shows the documented timing/response progression when actually run | verified, with a caveat now documented | Confirmed live twice (once during Phase 5, once during the `/simplify` re-verification) — the second run required more concurrent requests than the first, which is itself the finding written up as a new pattern |
| The graceful-degradation example shows all four documented behaviors against the same stopped-Account-Service state | verified | Confirmed live twice, most recently within the merged single-outage-window flow |

### What's Not Tested

Nothing new is untested — the entire point of this story was proving
existing, already-shipped behavior is accurate and reproducible, not
adding new behavior. The full backend test suite (94 tests, both
services) was re-run after every phase of this story to confirm the
documentation work introduced no regression, and passed every time.
