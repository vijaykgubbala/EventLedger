---
title: Raw-SQL CHECK constraints on decimal columns need an explicit CAST under SQLite
date: 2026-07-15
related: [../../architecture/data-model.md]
---

## Pattern

EF Core's SQLite provider stores `decimal` columns with `TEXT` affinity
(there's no native arbitrary-precision numeric storage class in SQLite, so
this is how precision is preserved). A `HasCheckConstraint(...)` call takes
a raw SQL fragment, and SQLite's comparison-affinity rules apply to that
fragment independently of what EF Core "knows" about the column's CLR
type — the fragment is just text handed to the SQLite engine.

For a comparison between a `TEXT`-affinity column and a bare numeric
literal (e.g. `"Amount" > 0`), SQLite's affinity rule converts the literal
to `TEXT` for the comparison, not the column to a number. The result is a
**lexicographic string comparison**, not a numeric one. Concretely: EF
Core round-trips `0m` to the string `"0.0"`, and `"0.0" > "0"` is `true` as
a string comparison (a string is "greater than" a proper prefix of
itself) — so a `CHECK ("Amount" > 0)` constraint silently passes for
`Amount = 0`, and would behave inconsistently for other values too (e.g.
it does not order negative or fractional decimals numerically). This does
not show up as a SQL error; the row simply gets inserted, and the
constraint has quietly stopped constraining anything.

## Guidance

Any raw-SQL `CHECK` constraint written against a `decimal` column under
SQLite must force a numeric comparison explicitly:

```csharp
entity.ToTable("events", t =>
    t.HasCheckConstraint("CK_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0"));
```

`CAST(... AS REAL)` overrides affinity inference and forces both sides of
the comparison to be evaluated numerically. This is safe for a `> 0`
positivity check — the precision `REAL` (double) loses is far below the
threshold where it could misclassify a genuinely positive amount as
non-positive or vice versa; it would only matter for a check that needed
to distinguish between two decimals differing in, say, the 15th
significant digit, which this system's validation never does.

This was caught by a genuinely red integration test in this project's TDD
cycle: `Insert_NonPositiveAmount_ThrowsDbUpdateException` failed with "no
exception was thrown" against the bare `"Amount" > 0` form, which is what
surfaced the affinity issue rather than it being caught by inspection.

## Examples

**Wrong** — passes for `Amount = 0` due to TEXT-affinity string comparison:

```csharp
t.HasCheckConstraint("CK_Amount_Positive", "\"Amount\" > 0")
```

**Right** — forces a numeric comparison regardless of the column's storage affinity:

```csharp
t.HasCheckConstraint("CK_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0")
```

Applied in both `GatewayDbContext` and `AccountDbContext` — see
[../../architecture/data-model.md](../../architecture/data-model.md) for
the `CHECK (amount > 0)` constraint this backs.
