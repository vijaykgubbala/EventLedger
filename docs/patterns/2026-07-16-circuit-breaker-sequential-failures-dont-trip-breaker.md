---
title: Sequential bounded-failure calls can starve the circuit breaker's sampling window
date: 2026-07-16
related: [../../architecture/resiliency.md, ../verification-guide.md]
---

# Sequential bounded-failure calls can starve the circuit breaker's sampling window

## Pattern

When a resilience pipeline's per-attempt bounded-failure latency is
comparable to (or larger than a meaningful fraction of) the circuit
breaker's sampling window, a caller that retries **sequentially** against
a failing dependency may never accumulate enough failures within one
window to trip the breaker — even though the dependency is failing
100% of the time.

In this system: each bounded failed call to the Account Service takes
~6.7–6.8s (2s timeout × 3 attempts + 200ms × 2 retry delays, per
`architecture/resiliency.md`'s configured values), and the circuit
breaker's sampling window is 10s. A caller firing requests one at a time,
waiting for each to complete before the next, produces roughly one
failure per 6.7s — well under the 4-call minimum throughput needed
within any single 10s window, since by the time enough sequential calls
have accumulated, the window has already rolled past the earliest ones.

Confirmed empirically while authoring
[docs/verification-guide.md](../verification-guide.md): firing calls one
at a time never tripped the breaker; firing them **concurrently** (so
several land within the same window) did. Even then, the exact number of
concurrent calls needed to trip it in practice was itself non-obvious
across separate live runs — a burst of 10 tripped it immediately once,
and required a second burst of 10 another time — evidence that the
window/throughput evaluation has enough timing sensitivity that a fixed
"10 is always enough" claim isn't safe to assert.

## Guidance

- Don't assume a documented `MinimumThroughput` number describes how
  many *sequential* retries it takes to trip a breaker — it describes
  concurrent/rapid-succession calls landing within the sampling window.
  A caller whose own failure mode is itself slow (bounded timeout +
  retry) may inadvertently never trip the breaker protecting it.
- When writing a manual/live demonstration of circuit-breaker behavior
  (not a unit test with a stubbed instant-fail handler), fire a burst of
  concurrent requests, not a sequential loop, and be prepared to repeat
  the burst if the first one doesn't visibly trip it — don't hard-code
  an exact "N concurrent calls always trips it" expectation into
  documentation or tooling.
- This is not a bug in the circuit breaker configuration — it's an
  emergent interaction between two independently-reasonable settings
  (a multi-second bounded-failure latency, and a sampling window in the
  same order of magnitude). If a future story wanted the breaker to trip
  reliably even under purely sequential failures, the window would need
  to be widened relative to the bounded-failure latency, or the
  minimum-throughput count lowered — a deliberate tradeoff against
  false-positive tripping, not something to change casually.

## Examples

See [docs/verification-guide.md](../verification-guide.md)'s
"Resiliency" section for the live commands and observed timings that
surfaced this.
