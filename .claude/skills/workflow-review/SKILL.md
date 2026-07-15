---
name: workflow-review
description: Run all review-* agents (correctness, dotnet, testing, security, maintainability) in parallel over the current change, merge their findings, and walk through them interactively. Use before committing a nontrivial change.
allowed-tools: Task
---

Dispatches every `review-*` agent over the same change simultaneously,
merges their JSON findings into one prioritized list, and walks the user
through them one at a time rather than dumping a wall of output.

## Steps

1. Determine the scope of the review — the current diff (`git diff`) if
   the user didn't specify a narrower set of files.
2. Dispatch all five review agents **in parallel, in a single batch** —
   not sequentially:
   - `review-correctness`
   - `review-dotnet`
   - `review-testing`
   - `review-security`
   - `review-maintainability`

   Give each the same change description/diff so they're reviewing
   identical scope.
3. Collect each agent's JSON `findings` array. Merge into one list, sorted
   by severity (`critical` first, then `warning`, then `suggestion`), and
   within a severity tier, group by file.
4. Deduplicate: if two agents flag the same line for essentially the same
   reason (this can happen between `review-correctness` and
   `review-dotnet`, e.g. a dropped `CancellationToken`), keep the more
   specific finding and note that it was corroborated by both.
5. Walk the user through the merged list **interactively** — present
   findings a few at a time (or one at a time for `critical` findings),
   not as one long dump. Ask whether to address each before moving to the
   next, rather than assuming the user wants every finding fixed.

## What this skill does not do

- It does not fix anything itself — every review agent is read-only by
  design. Fixes happen in the main conversation after the user decides
  which findings to act on.
- It does not replace `architecture-guide` for pre-implementation design
  checks — this skill reviews code that already exists or is already
  drafted, not a proposed plan.
