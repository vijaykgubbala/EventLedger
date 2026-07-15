# Deployment Architecture

Event Ledger runs **locally only** — there is no cloud environment, no
orchestration platform, and no CI/CD pipeline required by this system's
scope. This document covers the two supported ways to run it.

## Docker Compose (preferred)

A single `docker-compose.yml` at the repo root starts both services:

- One container per service, each built from that service's own
  `Dockerfile`.
- Each service's SQLite database file is written to a container-local path;
  optionally bind-mounted to a host directory so data survives a
  `docker compose down` (developer convenience, not a requirement — this
  system has no durability SLA to meet).
- The Gateway's container is configured with the Account Service's
  in-network base URL (Compose's service-name DNS, e.g.
  `http://account-service:8081`) via environment variable/configuration —
  not hardcoded — so the same image works whether run via Compose or
  pointed at a differently-hosted Account Service in manual mode.
- Both services expose their HTTP port to the host for local `curl`/test
  access; the Account Service's port exposure is a debugging convenience
  only — nothing outside the Compose network is expected to call it
  directly, consistent with it being "internal" per
  [vertical-architecture.md](vertical-architecture.md).

## Manual (no Docker)

Each service is an independently runnable ASP.NET Core process
(`dotnet run` from that service's project directory). Manual mode requires:

1. The .NET 8 SDK installed locally.
2. Starting the Account Service first (or configuring the Gateway to point
   at wherever it's listening) and the Gateway second, each on its own
   port, since the Gateway's outbound calls need the Account Service's base
   URL configured (same configuration mechanism as the Compose case above —
   an environment variable/`appsettings`, not a hardcoded URL).

Exact commands are in [README.md](../README.md) once the projects exist —
this document defines the topology; the README is the executable
instruction set.

## What this system deliberately does not have

- No Kubernetes manifests, Helm charts, or any orchestration platform — two
  processes on one machine do not need a scheduler.
- No Terraform/Terragrunt or any infrastructure-as-code — there is no cloud
  infrastructure to provision.
- No API gateway or reverse proxy in front of the Gateway — the Gateway
  *is* the system's single public entry point already; fronting it with
  another gateway would be a layer with no distinct responsibility.
- No CI/CD pipeline definition — out of scope for a take-home; tests are
  run locally via the command in [README.md](../README.md).
- No multi-environment (dev/staging/prod) configuration split — this system
  has exactly one deployment target: "running on the evaluator's or
  developer's machine."

## Anti-patterns to avoid

- **Do not hardcode the Account Service's URL in Gateway source code.** It
  must come from configuration (environment variable or `appsettings.json`)
  so Compose and manual/local runs can each supply the right value without
  a code change.
- **Do not add Kubernetes, Terraform, or any cloud-provider-specific
  deployment artifact.** This system's only supported targets are Docker
  Compose and manual local execution.
- **Do not put an API gateway, reverse proxy, or load balancer in front of
  the Event Gateway.** It is already the system's single public entry
  point.
- **Do not make the two services share a Docker network with any other
  system dependency (shared DB container, message broker container,
  etc.).** The only two containers this system needs are one per service.
