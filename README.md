# Event Ledger

A 2-service financial transaction system: a public-facing **Event Gateway**
and an internal **Account Service**, built to correctly handle out-of-order
and duplicate event delivery, and to degrade gracefully when the Account
Service is unavailable. Built against the requirements in
[docs/Assignment - Senior Software Engineer - AI.docx](docs/Assignment%20-%20Senior%20Software%20Engineer%20-%20AI.docx).

## Architecture overview

```
                          ┌───────────────────────┐
Client (curl/Postman) ──▶ │  Event Gateway API    │
                          │  (public-facing)      │
                          └──────────┬────────────┘
                                     │ REST (sync), traceparent propagated
                                     ▼
                          ┌───────────────────────┐
                          │  Account Service      │
                          │  (internal)           │
                          └───────────────────────┘
```

- **Event Gateway** — the only service exposed to clients. Validates
  incoming events, enforces idempotency on its own `events` table, calls
  the Account Service to apply the transaction, and persists the event
  locally only once that call is confirmed successful. Event reads
  (`GET /events/{id}`, `GET /events?account=...`) are served entirely from
  local data and keep working even if the Account Service is down.
- **Account Service** — internal-only, called solely by the Gateway. Owns
  account balance and transaction history, independently enforces its own
  idempotency, and computes balance on every read rather than storing a
  running total.

Each service is an independently runnable ASP.NET Core process with its own
file-based SQLite database — no shared database or in-process state between
them.

Full design rationale lives in [architecture/](architecture/); start with
[architecture/vertical-architecture.md](architecture/vertical-architecture.md).
Cross-cutting conventions (API contracts, event payload shape, naming,
logging, the backend project scaffold) live in [standards/](standards/).

## Tech stack

| Concern | Choice |
|---|---|
| Runtime / framework | ASP.NET Core, .NET 8, C# |
| Persistence | SQLite via EF Core, file-based (one DB per service) |
| Tracing | OpenTelemetry SDK (ASP.NET Core + `HttpClient` instrumentation, W3C `traceparent`) |
| Logging | Serilog, JSON console sink, `Enrich.FromLogContext()` |
| Resiliency | Polly v8 (circuit breaker + timeout, primary; short retry, secondary) |
| Service-to-service communication | Synchronous REST over HTTP |
| Local orchestration | Docker Compose (preferred) or manual `dotnet run` |

Rationale for each of these is in the correspondingly-named
[architecture/](architecture/) document — this table is a pointer, not the
explanation.

## API

### Event Gateway (public-facing)

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/events` | Submit a transaction event |
| `GET` | `/events/{id}` | Retrieve a single event by its ID |
| `GET` | `/events?account={accountId}` | List events for an account, ordered by `eventTimestamp` |
| `GET` | `/health` | Health check |

### Account Service (internal)

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/accounts/{accountId}/transactions` | Apply a transaction to an account |
| `GET` | `/accounts/{accountId}/balance` | Get the current balance for an account |
| `GET` | `/accounts/{accountId}` | Get account details and recent transactions |
| `GET` | `/health` | Health check |

Request/response shapes, status codes, and validation rules are defined in
[standards/api.md](standards/api.md) and [standards/events.md](standards/events.md).

## Resiliency

The Gateway wraps its call to the Account Service in a Polly v8 pipeline:
**circuit breaker + timeout as the primary pattern**, with a short retry
layered underneath for transient blips only. Circuit breaker + timeout was
chosen over a bare retry because a bare retry-with-backoff against a
genuinely down dependency reproduces the exact "hanging" failure mode the
assignment calls out as unacceptable — `timeout × attempts` of latency per
request during an outage is still an unbounded-feeling wait from the
client's perspective. The circuit breaker fails fast once the dependency is
confirmed down, keeping the Gateway responsive rather than merely
bounded-slow. Full rationale, including why bulkhead wasn't chosen as
primary for a system at this scale, is in
[architecture/resiliency.md](architecture/resiliency.md).

## Assumptions & bonus scope

- No pre-existing account registry — `accountId` is an implicit identity;
  any string is a valid account as soon as a transaction references it (see
  [architecture/data-model.md](architecture/data-model.md)).
- OpenTelemetry Collector + Jaeger/Zipkin trace visualization, a Prometheus
  metrics endpoint, exponential-backoff-with-jitter retry, Gateway rate
  limiting, contract tests, and an async local-queue fallback are all
  listed as bonus opportunities in the assignment and are treated as
  out-of-scope for the core implementation — see
  [architecture/observability.md](architecture/observability.md) and
  [architecture/resiliency.md](architecture/resiliency.md) for where each
  would plug in if pursued.
- This system runs locally or via Docker Compose only; there is no cloud
  deployment target — see
  [architecture/deployment-architecture.md](architecture/deployment-architecture.md).

## Setup

**Docker Compose path (primary, recommended):** only
[Docker](https://docs.docker.com/get-docker/) with Compose support (bundled
with Docker Desktop, or the `docker-compose-plugin` package on Linux) is
required — no local .NET SDK needed. See "Running the services" below.

**Manual / local-tooling path:** the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
is required if you want to run either service directly with `dotnet run`,
or run the automated test suite with `dotnet test` — see "Running the
services" and "Running the tests" below for the exact commands. No other
local dependencies (e.g. a separate database server) are needed; each
service creates its own file-based SQLite database automatically on
first run.

## Running the services

### Docker Compose (preferred)

```
docker compose up --build
```

Brings both services to a healthy state with no manual steps — the
Gateway's container waits for the Account Service's own healthcheck to
pass before starting (`depends_on: condition: service_healthy`), so
there's no cold-start race to work around.

- Event Gateway: `http://localhost:5099`
- Account Service: `http://localhost:5199` (internal-only by design;
  exposed here purely for local `curl`/debugging convenience)

```
curl http://localhost:5099/health
curl http://localhost:5199/health
```

Stop and remove both containers:

```
docker compose down
```

SQLite storage is ephemeral — data does not persist across
`docker compose down`/`up` cycles.

### Manual (no Docker)

Requires the .NET 8 SDK. Start the Account Service first, then the
Gateway — the Gateway's `AccountService:BaseUrl` config
(`http://localhost:5199` by default, in `appsettings.json`) expects it
already listening:

```
dotnet run --project src/EventLedger.AccountService
dotnet run --project src/EventLedger.Gateway
```

## Running the tests

```
dotnet test
```

Runs both test projects (94 tests total) with no manual setup — every
fixture creates and cleans up its own temp-file SQLite database.

The suite is checked against the assignment's required checklist item by
item:

| # | Requirement | Covered by |
|---|---|---|
| 1 | Idempotency (real SQLite) | `SubmitEventHandlerTests.cs`, `GatewayToAccountServiceFullFlowTests.cs` |
| 2 | Out-of-order handling | `EventsControllerTests.cs`, `GatewayToAccountServiceFullFlowTests.cs` |
| 3 | Balance computation incl. zero-transaction edge case | `BalanceQueryHandlerTests.cs` (Account Service) |
| 4 | Validation, each rejection case | `EventValidatorTests.cs` (unit level), `EventsControllerTests.cs` (HTTP level) |
| 5 | Resiliency (circuit breaker + `503`) | `EventsControllerTests.cs` |
| 6 | Trace propagation | `GatewayToAccountServiceFullFlowTests.cs` |
| 7 | Full Gateway→Account Service integration | `GatewayToAccountServiceFullFlowTests.cs` — 4 tests over two real, wired `WebApplicationFactory` instances |
| 8 | Runnable via `dotnet test`, no manual setup | this section — 94/94 passing, zero setup steps |

Full checklist definition:
[.claude/agents/review-testing.md](.claude/agents/review-testing.md).

For a manual, hands-on walkthrough of these same requirements against a
live Docker deployment — with concrete `curl` commands and real test
data — see
[docs/verification-guide.md](docs/verification-guide.md).
