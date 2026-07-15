---
title: Idempotency keys need a DB constraint, not a check-then-insert
date: 2026-07-15
related: [../../architecture/data-model.md, ../../architecture/vertical-architecture.md]
---

## Pattern

When an API accepts a caller-supplied idempotency key (like this system's
`eventId`), the temptation is to implement idempotency in application code:
query for an existing row with that key, and only insert if none is found.
This is wrong under concurrency, and it's wrong in a way that doesn't show
up in normal manual testing — it only surfaces when two requests with the
same key arrive close enough together to interleave.

## Guidance

Enforce the idempotency key as a **database-level `UNIQUE` constraint**,
and structure the write path as "attempt the insert, handle the
constraint-violation exception" rather than "check, then insert." The
database is the only component that can see both concurrent requests
atomically relative to the write; application code checking-then-inserting
has an unavoidable window between its read and its write where a second
request can slip through.

Concretely, in EF Core against SQLite this means: configure a `UNIQUE`
index on the key column in `OnModelCreating`, call `SaveChangesAsync()`
directly on the insert without a preceding existence query, and catch
`DbUpdateException` to detect the constraint violation, then re-fetch and
return the existing row. Don't try to inspect the exception for "was this
specifically a `UNIQUE` violation on this specific column" beyond what's
needed to distinguish it from other `DbUpdateException` causes — if the
table has exactly one `UNIQUE` constraint (as both of this system's tables
do), any `UNIQUE` violation on that insert is the duplicate-key case by
construction.

## Examples

**Wrong** — races under concurrent duplicate submission:

```csharp
var existing = await _db.Events.FirstOrDefaultAsync(e => e.EventId == eventId);
if (existing is not null) return existing; // gap here: two requests can both pass this check
_db.Events.Add(newEvent);
await _db.SaveChangesAsync(); // both can reach here for the same eventId
```

**Right** — the constraint is the arbiter, not application logic:

```csharp
try
{
    _db.Events.Add(newEvent);
    await _db.SaveChangesAsync();
    return newEvent; // this request created it
}
catch (DbUpdateException)
{
    return await _db.Events.SingleAsync(e => e.EventId == eventId); // another request won the race
}
```

This is the pattern applied by both services in this system — see
[../../architecture/vertical-architecture.md](../../architecture/vertical-architecture.md#core-decision-idempotency-via-db-level-unique-constraint)
and [../../architecture/data-model.md](../../architecture/data-model.md).
