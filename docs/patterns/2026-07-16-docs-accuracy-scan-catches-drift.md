---
title: A light accuracy scan against source, not assumed correctness, is what catches doc drift
date: 2026-07-16
related: [../plans/10_readme-finalization-plan.md]
---

# A light accuracy scan against source, not assumed correctness, is what catches doc drift

## Pattern

When a documentation-finalization story only needs to touch the parts
of a doc explicitly called out as incomplete (a `TODO` stub, a stale
callout), it's tempting to assume the rest of the doc — written by
earlier, already-reviewed stories — is still accurate and leave it
untouched. That assumption is cheap to make and easy to be wrong about:
a section can go stale silently when a *later* story adds new behavior
without anyone circling back to update docs written before that
behavior existed.

## Guidance

- For any docs-finalization or docs-audit story, do a **light
  read-through of every section against current source** — not a
  rewrite, just a scan — even for sections nobody flagged as broken.
  The cost is minutes (reading a handful of already-short sections
  against the files they describe); the payoff is catching exactly the
  kind of gap that "nothing was flagged" would otherwise hide.
- Concretely, in issue #10's execution: the README's Setup section was
  the only section explicitly flagged (a literal `TODO` stub). A scan
  of the *other four* sections — not flagged, assumed fine — found the
  API table was missing `GET /accounts/{accountId}/balance`, a real
  endpoint that had existed since issue #6/#7's resiliency work but was
  never back-filled into the README once it shipped. No story's plan
  ever said "update the README" when that endpoint was added, because
  it wasn't in scope for issue #6/#7 — the gap only became visible when
  something later deliberately re-checked doc against source instead of
  trusting it.
- The other three sections scanned in that same pass (Tech stack,
  Resiliency, Assumptions) were found to already be accurate. That's
  the expected, common outcome of doing this — most scans find nothing,
  which is fine. The value isn't in expecting to always find a gap;
  it's in converting an assumption ("probably still fine") into a
  verified fact cheaply enough that skipping the check has no real
  justification.

## Examples

See [docs/plans/10_readme-finalization-plan.md](../plans/10_readme-finalization-plan.md)'s
Phase 2 — the brainstorm explicitly weighed "targeted fix only" against
"targeted fix + light accuracy scan" as two approaches and recommended
the scan specifically because it converts an assumption into a fact for
a marginal few minutes of cost.
