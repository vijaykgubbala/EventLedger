# Backend Project Scaffold

Concrete project layout for the follow-up code pass. Each service is a
**single ASP.NET Core project with folder-based layering** — not a
multi-assembly split. Four endpoints per service does not justify separate
`.Domain.dll`/`.Application.dll`/`.Infrastructure.dll` projects; folders
inside one project give the same separation of concerns with none of the
project-reference/build-graph overhead. Revisit this only if a service's
endpoint count grows well past what the assignment scopes — see
[../architecture/vertical-architecture.md](../architecture/vertical-architecture.md#why-two-services-and-no-more)
for the same reasoning applied at the service-count level.

## Repository tree (code, once the follow-up pass adds it)

```
EventLedger/
├── EventLedger.sln
├── docker-compose.yml
├── src/
│   ├── EventLedger.Gateway/
│   │   ├── EventLedger.Gateway.csproj
│   │   ├── Program.cs
│   │   ├── Dockerfile
│   │   ├── appsettings.json
│   │   ├── Controllers/
│   │   ├── Application/
│   │   ├── Domain/
│   │   ├── Infrastructure/
│   │   └── Middleware/
│   └── EventLedger.AccountService/
│       ├── EventLedger.AccountService.csproj
│       ├── Program.cs
│       ├── Dockerfile
│       ├── appsettings.json
│       ├── Controllers/
│       ├── Application/
│       ├── Domain/
│       ├── Infrastructure/
│       └── Middleware/
└── tests/
    ├── EventLedger.Gateway.Tests/
    └── EventLedger.AccountService.Tests/
```

## File-placement table

Applies identically to both `EventLedger.Gateway` and
`EventLedger.AccountService` — the folder names are the same even though
the contents differ per service.

| Folder | Contains | Does not contain |
|---|---|---|
| `Controllers/` | ASP.NET Core controllers/minimal-API endpoint definitions; request/response DTO records; route attributes | Business logic, validation rules, direct `DbContext` usage |
| `Application/` | Use-case/handler classes orchestrating a single operation (e.g. `SubmitEventHandler`, `ApplyTransactionHandler`); validation logic | HTTP concerns (status codes, `HttpContext`), EF Core query syntax |
| `Domain/` | Plain domain model types (`Event`, `Transaction`), value objects, domain-level constants (e.g. the `CREDIT`/`DEBIT` type enum) | Any framework dependency — this folder should not reference ASP.NET Core, EF Core, or Polly types |
| `Infrastructure/` | EF Core `DbContext` and entity configuration, repository implementations, the Account Service `HttpClient` + Polly pipeline registration (Gateway only) | Business/validation logic — infrastructure implements interfaces defined by `Application`/`Domain`, it doesn't decide business rules |
| `Middleware/` | Cross-cutting ASP.NET Core middleware: the `LogContext` trace-ID push described in [logging-dotnet.md](logging-dotnet.md), global exception handling | Per-endpoint logic specific to one route |

## Where cross-cutting concerns live

| Concern | Project location |
|---|---|
| EF Core `DbContext` + SQLite connection setup | `Infrastructure/` (per service) |
| Serilog configuration | `Program.cs`, per [logging-dotnet.md](logging-dotnet.md) |
| OpenTelemetry SDK registration (tracing; metrics is issue #5's scope) | `Infrastructure/` (per service, folded into `AddGatewayInfrastructure()`/`AddAccountServiceInfrastructure()`), per [../architecture/observability.md](../architecture/observability.md) — matches every other DI/infra registration in this table, not `Program.cs` |
| Polly resilience pipeline for the Account Service `HttpClient` | `Infrastructure/` in `EventLedger.Gateway` only — the Account Service has no outbound calls, per [service-boundaries.md](service-boundaries.md) |
| `UNIQUE` constraint on `event_id` | EF Core entity configuration in `Infrastructure/` (per service), per [../architecture/data-model.md](../architecture/data-model.md) |
| Request validation | `Application/` (per service), per [api.md](api.md#validation-rules-post-events) |

## Test project layout

One test project per service (`EventLedger.Gateway.Tests`,
`EventLedger.AccountService.Tests`), plus the Gateway's test project is
where the one required full-flow integration test lives (Gateway →
Account Service), since that's the only place both services are exercised
together. Coverage expectations and the `dotnet test` command are covered
by the `test-dotnet` skill
([.claude/skills/test-dotnet/SKILL.md](../.claude/skills/test-dotnet/SKILL.md)).

## Anti-patterns to avoid

- **Do not split either service into multiple class-library projects**
  (`.Domain.csproj`, `.Application.csproj`, etc.). Folder-based layering
  inside one project is the deliberate choice for this system's size — see
  above.
- **Do not put EF Core, ASP.NET Core, or Polly types in `Domain/`.** If a
  domain type needs a framework attribute to compile, it's not a domain
  type — it belongs in `Infrastructure/` or `Controllers/`.
- **Do not put validation logic in `Controllers/`.** Controllers parse the
  request and call into `Application/`; they don't decide whether an
  `amount` is valid.
- **Do not add a `Services/` folder as a catch-all alongside
  `Application/`.** One folder for orchestration logic per service is
  enough at this scale; a second, vaguely-named folder next to it invites
  arbitrary placement decisions.
- **Do not register the Polly pipeline or `HttpClient` for the Account
  Service inside the Account Service project.** It's a Gateway-only
  outbound concern — see [service-boundaries.md](service-boundaries.md).
