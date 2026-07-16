---
title: "HealthController injected DbContext directly, violating the Controllers/Application layering rule"
date: 2026-07-16
category: layering
related: [../../../standards/backend-architecture.md, ../../plans/5_observability-plan.md, ../../reviews/5_observability.json]
---

# HealthController injected DbContext directly, violating the Controllers/Application layering rule

## Symptoms

`workflow-review` flagged both services' `HealthController` — not a
runtime bug, a design-convention violation caught by static review
against `standards/backend-architecture.md`.

## Root Cause

When `GET /health` was upgraded from a trivial placeholder (issue #3) to
a real DB-connectivity check (issue #5), the new code took the shortest
path: inject the service's `DbContext` straight into the controller and
call `Database.CanConnectAsync()` from the action method.

```csharp
public class HealthController(GatewayDbContext db) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" });
    }
}
```

`standards/backend-architecture.md`'s file-placement table explicitly
lists "direct `DbContext` usage" under what `Controllers/` **does not**
contain — that responsibility belongs in `Application/`, the same as
every other endpoint in both services (`EventQueryHandler`,
`SubmitEventHandler`, `ApplyTransactionHandler`, `BalanceQueryHandler`,
`AccountDetailsHandler` all already follow this). Because the check
itself was trivial (one line), it was easy to treat the rule as
overkill for this case and skip it — but doing so set a precedent: the
next contributor adding a slightly-less-trivial controller-level DB
check now has an in-repo example that contradicts the documented rule.

## Solution

Extract a one-method `HealthCheckHandler` per service into `Application/`,
matching the existing handler shape exactly (sealed class, primary-constructor
DI):

```csharp
public sealed class HealthCheckHandler(GatewayDbContext db)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        db.Database.CanConnectAsync(cancellationToken);
}
```

Register it via `AddScoped<HealthCheckHandler>()` alongside the other
handlers, and have the controller inject and call the handler instead of
the `DbContext`:

```csharp
public class HealthController(HealthCheckHandler healthCheckHandler) : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var canConnect = await healthCheckHandler.CanConnectAsync(cancellationToken);
        return Ok(new { status = canConnect ? "ok" : "degraded", database = canConnect ? "ok" : "unreachable" });
    }
}
```

Pure refactor — no observable behavior change. The existing
`HealthControllerTests.cs` (both the DB-reachable and DB-unreachable
cases, both services) passed unchanged, confirming the extraction didn't
alter behavior.

## Key Insight

"The logic is only one line" is not a reason to skip an established
layering rule — the rule exists to keep every endpoint's shape
predictable for the next reader, and a one-line exception is exactly as
confusing as a ten-line one the first time someone copies it as
precedent.

## Prevention

When adding *any* new controller action, check whether it touches a
`DbContext` directly before writing the action body — if it does, the
handler-extraction step belongs in the same commit, not a follow-up. The
architecture pre-flight check (`architecture-guide` skill, run during
`workflow-plan`/`workflow-execute`) is the intended gate for this; it
flagged no conflict here because the *design* (a DB check on `/health`)
was correct — only the *implementation detail* (which layer holds the
`DbContext` call) drifted, which is exactly what a code-level review
catches that a design-level pre-flight does not.

## Related Docs

- [standards/backend-architecture.md](../../../standards/backend-architecture.md) — the file-placement rule this violated.
- [docs/plans/5_observability-plan.md](../../plans/5_observability-plan.md) — OBS-4 implementation plan.
- [docs/reviews/5_observability.json](../../reviews/5_observability.json) — findings F3/F4, the review pass that caught this.
