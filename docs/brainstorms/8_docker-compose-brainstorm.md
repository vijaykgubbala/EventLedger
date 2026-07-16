# Brainstorm: Docker Compose — both services runnable via docker compose up

**Date:** 2026-07-16
**Issue:** #8

## Problem Statement

An evaluator needs to start the whole EventLedger system with one
command (`docker compose up --build`) and have both services reach a
healthy state (`GET /health` → `200` on both) with no manual steps. The
Gateway's Account Service base URL must come from configuration, not be
hardcoded, so the same image works in Compose or pointed at a
differently-hosted Account Service. Manual (non-Docker) startup
instructions must also be documented as a fallback (DEP-5).

## Codebase Context

- **`architecture/deployment-architecture.md`** (74 lines, read in full)
  is the sole owner of this design — no separate `standards/deployment*.md`
  exists. Key quotes:
  - "One container per service, each built from that service's own
    `Dockerfile`."
  - "The Gateway's container is configured with the Account Service's
    in-network base URL (Compose's service-name DNS, e.g.
    `http://account-service:8081`) via environment variable/configuration
    — not hardcoded."
  - SQLite persistence via bind-mount is "developer convenience, not a
    requirement — this system has no durability SLA to meet."
  - Anti-patterns: no Kubernetes/Terraform/cloud artifacts, no reverse
    proxy in front of the Gateway, no shared Docker network with any
    other system dependency — "the only two containers this system needs
    are one per service."
  - Manual-mode ordering: start the Account Service first, then the
    Gateway, each pointed at the other via the same configuration
    mechanism as the Compose case (env var/`appsettings`), not a
    hardcoded URL.
- **No Docker artifacts exist yet** — greenfield for this story.
- **`Gateway/appsettings.json`** already has `AccountService:BaseUrl`
  as a config value (`http://localhost:5199`, correct for manual mode)
  — already not hardcoded in source, per the existing anti-pattern rule.
  A Compose-supplied override via `AccountService__BaseUrl` (ASP.NET
  Core's standard double-underscore env-var-to-config binding) is what
  makes the same image work in both run modes.
- Both services' SQLite connection strings are relative paths
  (`gateway.db`, `accountservice.db`) — resolved relative to the
  container's working directory; no volume/absolute-path config exists
  yet, consistent with the "ephemeral is fine" framing above.
- **`launchSettings.json`** dev ports: Gateway `http://localhost:5099`,
  Account Service `http://localhost:5199` — unrelated to container ports
  but reusable for host-side Compose port mapping if desired.
- Both `.csproj` files: `Sdk="Microsoft.NET.Sdk.Web"`, `net8.0`, no
  existing Docker/container SDK properties, no project references
  between the two services — each needs its own independent build stage.
- **`GET /health`** (both services) always returns HTTP `200`, even when
  the database is unreachable — only the JSON body's `status` field
  flips to `"degraded"`. A Compose healthcheck keyed on status code alone
  proves the process booted and is serving requests, not DB reachability
  — resolved in Q&A below.
- **`standards/backend-architecture.md`**'s planned repo tree already
  shows the expected file locations: `docker-compose.yml` at repo root,
  `Dockerfile` inside each service's own folder
  (`src/EventLedger.Gateway/Dockerfile`,
  `src/EventLedger.AccountService/Dockerfile`).
- **`README.md`** already has a "Running the services" section with both
  a Docker Compose and a manual sub-section pre-titled `**TODO**` —
  this story completes that section, not new structure.
- `.gitignore` has no entries that would conflict with adding
  `Dockerfile`/`docker-compose.yml`/`.dockerignore`.
- No prior `docs/patterns/`/`docs/solutions/` entries touch Docker or
  containerization — this is new ground for the project's knowledge base.

## Q&A Decisions

**Q1: How should the Compose healthcheck verify "healthy state," given `/health` always returns 200 even when the DB is unreachable?**
A: Status-code only — `curl`-style check for a 2xx response. Matches DEP-4's literal wording ("`GET /health` → `200`"); DB connectivity isn't a meaningful cold-start risk here since each container creates its own SQLite file locally on first run.

**Q2: Should `docker-compose.yml` bind-mount volumes so the SQLite files survive `docker compose down`?**
A: No — ephemeral, container-local storage only. Matches the architecture doc's own "not a requirement" framing and this project's YAGNI stance.

**Q3: What host ports should Compose expose?**
A: Reuse the existing dev-mode ports (5099 Gateway, 5199 Account Service) as the host-side mapping, so an evaluator already used to those addresses in manual mode gets the same ones via Compose.

**Q4: How much of the README should this story fill in?**
A: Only the "Running the services" section (both Docker and manual sub-sections) — exactly what DEP-4/DEP-5 ask for. "Setup" and "Running the tests" stay `TODO` for issue #10, which explicitly owns the full README/handover pass.

## Proposed Approaches

### Approach 1: Two independent multi-stage Dockerfiles + one Compose file (Recommended)

Each service gets its own standard ASP.NET Core multi-stage Dockerfile
(`mcr.microsoft.com/dotnet/sdk:8.0` build stage →
`mcr.microsoft.com/dotnet/aspnet:8.0` runtime stage), built and published
independently. `docker-compose.yml` at repo root defines two services
(`gateway`, `account-service`), wires the Gateway's
`AccountService__BaseUrl` env var to `http://account-service:8081`
(Compose service-name DNS), maps host ports 5099/5199, sets
`ASPNETCORE_URLS=http://+:8080` (or the per-service port) so Kestrel
binds to all interfaces inside the container, and uses
`depends_on: account-service: condition: service_healthy` so the Gateway
only starts after the Account Service's own healthcheck passes —
mirroring the architecture doc's manual-mode "start Account Service
first" ordering, enforced automatically instead of left to chance.

**Pros:**
- Matches the architecture doc's explicit "one container per service"
  mandate exactly, no interpretation needed.
- Each Dockerfile is small, standard, and independently
  understandable — no shared build-stage cleverness to reason about.
- `depends_on` + `service_healthy` gives deterministic startup ordering
  for free, directly satisfying DEP-4's "no manual steps" requirement
  (no race where the Gateway's first health-driven readiness check hits
  a not-yet-up Account Service).

**Cons:**
- Two Dockerfiles have near-identical boilerplate (same SDK/runtime
  images, same `dotnet restore`/`publish` shape) — acceptable duplication
  for two files that will rarely change, not worth abstracting away.

### Approach 2: Single multi-stage Dockerfile with build targets

One `Dockerfile` at repo root defines a shared base/build stage, then two
named final stages (`gateway`, `account-service`) selected via
`docker build --target`. `docker-compose.yml`'s `build:` block for each
service points at the same Dockerfile with a different `target:`.

**Pros:**
- Removes the boilerplate duplication Approach 1 accepts.

**Cons:**
- More Docker-specific cleverness (multi-target builds) for a two-service,
  ~0.25h-budget story where the "duplication" being removed is about
  15 lines of nearly-static SDK/runtime boilerplate per file — the
  complexity cost isn't worth the line count saved.
- Muddies the "one Dockerfile per service" expectation already documented
  in `standards/backend-architecture.md`'s repo tree, which explicitly
  lists a separate `Dockerfile` under each service's own folder.
- Against this project's stated YAGNI stance for a saving this marginal.

### Approach 3: One container running both services (rejected outright)

A single container starts both `dotnet` processes (e.g. via a shell
script or process supervisor).

**Pros:**
- Fewer moving Compose/Docker parts.

**Cons:**
- Directly contradicts `architecture/deployment-architecture.md`'s
  explicit, unambiguous decision: "One container per service, each built
  from that service's own `Dockerfile`... the only two containers this
  system needs are one per service." Not a legitimate option — included
  here only to show it was considered and is disqualified by an existing
  recorded decision, not by preference.

## Recommendation

**Approach 1.** It's the only option that doesn't either violate the
architecture doc's explicit "one container per service" decision
(Approach 3) or add unrequested build-system cleverness against this
project's YAGNI stance (Approach 2). All four Q&A decisions point the
same direction: the simplest Compose setup that satisfies DEP-4/DEP-5
literally, with `depends_on`/`service_healthy` doing the one piece of
real orchestration work (startup ordering) the architecture doc actually
calls for.

## Related Docs

- [architecture/deployment-architecture.md](../../architecture/deployment-architecture.md)
- [standards/backend-architecture.md](../../standards/backend-architecture.md)
- [docs/plans/3_service-separation-plan.md](../plans/3_service-separation-plan.md)
- [docs/plans/5_observability-plan.md](../plans/5_observability-plan.md) (health-check implementation being wired into the Compose healthcheck)
