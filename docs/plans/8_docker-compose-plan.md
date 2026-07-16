# Docker Compose — both services runnable via docker compose up

**Issue:** #8

## Context

Builds on the brainstorm at
[docs/brainstorms/8_docker-compose-brainstorm.md](../brainstorms/8_docker-compose-brainstorm.md).
Two independent multi-stage Dockerfiles (one per service, per
`architecture/deployment-architecture.md`'s explicit "one container per
service" decision), one root-level `docker-compose.yml`, and a completed
README "Running the services" section. No C# application code changes —
`AccountService:BaseUrl` is already config-driven (not hardcoded); this
story only supplies a Compose-scoped environment override at runtime.

## Relevant Learnings

No prior `docs/solutions/` entries touch Docker or containerization —
this is new ground for the project's knowledge base. Architecture
pre-flight (via `architecture-guide`) found no conflicts; one advisory
note carried into this plan: the `GET /health` endpoint always returns
`200` even when the database is unreachable (established during issue #5's work), so the Compose
`healthcheck:`/`depends_on: condition: service_healthy` gate proves the
process is up and listening — a **readiness** gate, not a **health**
gate. This is intentional per the brainstorm's Q1 decision, not a gap to
fix here.

## Implementation Steps

### Phase 1: Dockerfiles

- [x] Write `src/EventLedger.Gateway/Dockerfile`: multi-stage build.
  Build stage `mcr.microsoft.com/dotnet/sdk:8.0` — `COPY *.csproj ./`,
  `dotnet restore`, `COPY . .`, `dotnet publish -c Release -o /app/publish`.
  Runtime stage `mcr.microsoft.com/dotnet/aspnet:8.0` — `WORKDIR /app`,
  `COPY --from=build /app/publish .`, `RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*`
  (the base runtime image does not include `curl`; Phase 2's healthcheck
  needs it — installing it here rather than assuming it's present is
  what makes the brainstorm's Q1-chosen status-code-only check actually
  work), `ENV ASPNETCORE_HTTP_PORTS=8080`, `ENTRYPOINT ["dotnet", "EventLedger.Gateway.dll"]`.
- [x] Write `src/EventLedger.AccountService/Dockerfile`: identical shape,
  `ENV ASPNETCORE_HTTP_PORTS=8081` (matches
  `architecture/deployment-architecture.md`'s illustrative
  `http://account-service:8081` example), `ENTRYPOINT ["dotnet", "EventLedger.AccountService.dll"]`.
- [x] Write `src/EventLedger.Gateway/.dockerignore` and
  `src/EventLedger.AccountService/.dockerignore` (each scoped to its own
  build context — see Phase 2's per-service `context:` — excluding
  `bin/`, `obj/`, `**/TestResults/`): keeps each build context small and
  fast, and guarantees no accidental cross-service or test-project
  leakage into either image.
  - Both Dockerfiles confirmed to build successfully in isolation
    (`docker build` against each service's own directory; images removed
    after verification — no `docker-compose.yml` yet at this point).

### Phase 2: `docker-compose.yml`

- [ ] Write `docker-compose.yml` at repo root:
  - `account-service`: `build: context: ./src/EventLedger.AccountService`,
    `ports: ["5199:8081"]`,
    `environment: ASPNETCORE_HTTP_PORTS=8081` (redundant with the
    Dockerfile's own `ENV` but explicit here for readability),
    `healthcheck: test: ["CMD", "curl", "-f", "http://localhost:8081/health"], interval: 5s, timeout: 3s, retries: 5, start_period: 10s`.
  - `gateway`: `build: context: ./src/EventLedger.Gateway`,
    `ports: ["5099:8080"]`,
    `environment: AccountService__BaseUrl=http://account-service:8081`
    (Compose service-name DNS — the env-var override the brainstorm's
    codebase research confirmed is the correct mechanism, per
    `architecture/deployment-architecture.md`'s explicit guidance and its
    "do not hardcode" anti-pattern),
    `healthcheck: test: ["CMD", "curl", "-f", "http://localhost:8080/health"], interval: 5s, timeout: 3s, retries: 5, start_period: 10s`,
    `depends_on: account-service: condition: service_healthy` (enforces
    the architecture doc's manual-mode "start Account Service first"
    ordering automatically, rather than leaving it to chance — satisfies
    DEP-4's "no manual steps").
  - No `volumes:` block (ephemeral SQLite storage, per the brainstorm's
    Q2 decision).

### Phase 3: README

- [ ] Fill in `README.md`'s existing `**TODO — Docker Compose:**` line
  under "Running the services" with the real command
  (`docker compose up --build`) plus one line each on what to expect
  (both services healthy, ports 5099/5199 reachable) and how to stop
  (`docker compose down`).
- [ ] Fill in the existing `**TODO — manual:**` lines with the real,
  already-correct commands
  (`dotnet run --project src/EventLedger.AccountService` then
  `dotnet run --project src/EventLedger.Gateway`, in that order — mirrors
  the architecture doc's manual-mode ordering guidance), noting the
  Account Service must be started first since the Gateway's
  `AccountService:BaseUrl` in `appsettings.json` already points at its
  dev port. Do not touch the "Setup" or "Running the tests" `TODO`
  sections — out of scope per the brainstorm's Q4 decision (reserved for
  issue #10).

### Phase 4: Manual verification (documented, not automated)

- [ ] Run `docker compose up --build` locally. Confirm both containers
  reach a healthy state with no manual intervention (`docker compose ps`
  shows both as `healthy`).
- [ ] `curl http://localhost:5099/health` and
  `curl http://localhost:5199/health` — confirm both return `200`.
- [ ] `curl -X POST http://localhost:5099/events` with a valid payload,
  then `curl http://localhost:5099/events/{eventId}` — confirm the
  full flow works end-to-end through both containers (proves the
  `AccountService__BaseUrl` override actually resolves and the two
  containers can reach each other over the Compose network).
- [ ] `docker compose down` — confirm clean teardown.
- [ ] Record the exact commands run and their output in the handoff's
  Test Coverage section (this story has no `docs/plans/*-plan.md`
  Testing Strategy test-case table in the usual xUnit sense — see below).

## Testing Strategy

### Test Environment

Not applicable in the usual xUnit sense — per the brainstorm's
verification-approach decision, this story's changes (Dockerfiles,
Compose config, README prose) have no C# code for `dotnet test` to
exercise. Verification is manual, per Phase 4 above, with commands and
output recorded in the handoff rather than as a new automated test file.

### Test Cases

- **Description**: `docker compose up --build` brings both services to a
  healthy state with no manual steps (DEP-4).
  **Type**: Manual. **Phase reference**: Phase 4.
- **Description**: Gateway's Account Service base URL resolves via
  Compose service-name DNS, not a hardcoded value (already true in
  source; this proves the runtime override works).
  **Type**: Manual (proven by the end-to-end `POST /events` check).
  **Phase reference**: Phase 4.
- **Description**: Manual (non-Docker) startup instructions in the
  README are accurate and sufficient as a fallback (DEP-5).
  **Type**: Manual — cross-checked against the already-passing
  `dotnet test` suite's own dual-service integration tests
  (`GatewayToAccountServiceFullFlowTests.cs`), which already prove the
  two services can talk to each other when started in-process; Phase 4
  proves it again as two genuinely separate `dotnet run` processes.
  **Phase reference**: Phase 3.

## Decisions Made

- **Runtime image needs `curl` installed explicitly.** The official
  `mcr.microsoft.com/dotnet/aspnet:8.0` image doesn't include it by
  default; the brainstorm's Q1 (status-code-only healthcheck via `curl -f`)
  only works if it's present. Installed via `apt-get` in each Dockerfile's
  runtime stage — a small, standard, one-line addition, not a new
  dependency worth asking about.
- **Container-internal ports: `8080` (Gateway), `8081` (Account
  Service).** Matches `architecture/deployment-architecture.md`'s only
  concrete illustrative example (`http://account-service:8081`) exactly,
  avoiding any doc/implementation drift. Host-side mapping reuses the
  existing dev ports (5099/5199) per the brainstorm's Q3.
- **Per-service build context** (`./src/EventLedger.Gateway`,
  `./src/EventLedger.AccountService`), not the repo root. Since neither
  service references the other or any shared project, scoping each
  build context to just that service's own folder keeps builds fast and
  makes each Dockerfile self-contained with simple relative `COPY`
  paths — no risk of accidentally pulling the other service, tests, or
  `.git` into an image.

### Known Constraints

- No SQLite persistence across `docker compose down`/`up` cycles — by
  design (brainstorm's Q2), not an oversight.
