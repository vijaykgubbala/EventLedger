---
title: EF Core's SQLite provider can't translate ORDER BY / SUM over DateTimeOffset or decimal
date: 2026-07-15
related: [../../architecture/account-architecture.md, ../../standards/events.md]
---

## Pattern

Two related, non-obvious EF Core SQLite provider limitations surfaced
during Phase 3 of issue #2 (`AccountDetailsHandler`, `BalanceQueryHandler`):

1. **`OrderBy`/`OrderByDescending` on a `DateTimeOffset` column throws
   `NotSupportedException`** at query-execution time — "SQLite does not
   support expressions of type 'DateTimeOffset' in ORDER BY clauses."
   This is not a compile-time error; the LINQ compiles fine and only fails
   when the query actually runs.
2. **`SumAsync`/`AverageAsync` over a `decimal` column also throws
   `NotSupportedException`** — "SQLite cannot apply aggregate operator
   'Sum' on expressions of type 'decimal'." Same failure mode: compiles,
   fails at execution.

Both stem from the same root cause: EF Core's SQLite provider stores
`DateTimeOffset` and `decimal` with `TEXT` affinity to preserve fidelity
(SQLite has no native types for either), and the provider does not attempt
to translate ordering or aggregation over those `TEXT`-affinity
representations into correct SQL — it refuses outright rather than risk
silently wrong results.

## Guidance

For both cases, **materialize the rows first, then use LINQ to Objects**
for the ordering or aggregation:

```csharp
// Wrong — throws NotSupportedException at runtime:
var ordered = await db.Transactions
    .Where(t => t.AccountId == accountId)
    .OrderBy(t => t.AppliedAt)
    .ToListAsync(cancellationToken);

// Right — filter server-side, order client-side:
var rows = await db.Transactions
    .Where(t => t.AccountId == accountId)
    .ToListAsync(cancellationToken);
var ordered = rows.OrderBy(t => t.AppliedAt).ToList();
```

Keep any filtering (`Where`) that doesn't touch the problematic column
server-side — only the ordering/aggregation step needs to move client-side.
This system's scale (a handful of rows per account, single-process, local
SQLite) means the cost of materializing before ordering/summing is
negligible; this is not a workaround to revisit later, it's the right
approach at this scale.

**This will recur** anywhere a query orders by `EventRecord.EventTimestamp`
(both are `DateTimeOffset`) — notably the Gateway's
`GET /events?account=...` listing, which
[standards/events.md](../../standards/events.md) requires to be ordered by
`eventTimestamp` ascending. When implementing that endpoint, apply the same
materialize-then-order pattern rather than rediscovering this limitation.

## Examples

See `BalanceQueryHandler.GetBalanceAsync` and
`AccountDetailsHandler.GetDetailsAsync` in
`src/EventLedger.AccountService/Application/` for the applied fix — the
`decimal` sum is also documented in
[../../architecture/account-architecture.md](../../architecture/account-architecture.md#balance-computation).
