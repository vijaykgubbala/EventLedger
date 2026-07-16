---
issue: 8
issue_url: https://github.com/vijaykgubbala/EventLedger/issues/8
branch: 8_docker-compose
base: master
plan: docs/plans/8_docker-compose-plan.md
---

# Handoff: Story 7 — Docker Compose

## Release Notes

An evaluator can now bring the whole system up with one command:
`docker compose up --build`. Both services build from their own
multi-stage Dockerfile, and the Gateway waits for the Account Service's
own healthcheck to pass before it starts — no manual "start this one
first" step, no cold-start race.

The Gateway's Account Service base URL was already config-driven (never
hardcoded in source), so wiring the two containers together was a matter
of supplying the right runtime value: Compose's own service-name DNS
(`http://account-service:8081`), passed in as an environment variable
override. Both services keep their existing dev-mode host ports
(5099/5199) so an evaluator who's already used those addresses in manual
mode sees the same ones under Compose. SQLite storage is intentionally
ephemeral — this system has no durability requirement to meet, and adding
volume mounts would be complexity with no corresponding acceptance
criterion asking for it.

The manual (non-Docker) fallback documented in the README was already
correct in principle (both services are ordinary `dotnet run` processes),
it just needed to be written down — the README's "Running the services"
section now has both paths spelled out with real commands.

This branch also includes a `/workflow-review` pass with three security/
correctness fixes worth calling out specifically, since they weren't part
of the original plan: the Account Service's port was originally bound to
all network interfaces rather than just the local machine, which would
have let anyone on the same network bypass the Gateway entirely and
mutate account balances directly — now bound to `127.0.0.1` only. Both
`.dockerignore` files were also missing an exclusion for local SQLite
files, meaning a `docker compose up --build` run in a working copy that
had previously been started manually (leaving a `gateway.db` on disk)
could have silently baked that stale database into the image, undermining
the "ephemeral storage" this story otherwise guarantees — both fixes were
verified empirically, not just reasoned about.

## Risk Analysis

| Area | Blast Radius | Reviewer Focus | Mitigation |
|---|---|---|---|
| Dockerfiles (`src/EventLedger.Gateway/Dockerfile`, `src/EventLedger.AccountService/Dockerfile`) | Small — new files, no changes to any existing `.cs`/`.csproj` | Whether the multi-stage build actually produces a working image, and whether the runtime image is reasonably lean | Both images were built and run standalone before `docker-compose.yml` existed, then re-verified as part of the full Compose stack; multi-stage build discards the ~800MB SDK layer, keeping only the aspnet runtime + `curl` in the final image |
| `docker-compose.yml` orchestration + network exposure | Small — new file, root-level | Whether the Gateway genuinely reaches the Account Service only via the documented mechanism (Compose DNS, not a hardcoded value), and whether either service is reachable from outside the host machine | End-to-end `POST /events` → `GET /events/{id}` → `GET /accounts/{id}/balance` flow proven through both containers; port bindings scoped to `127.0.0.1` after the review pass caught the original all-interfaces bind (see Release Notes) |
| `.dockerignore` / build-context correctness | Small — two 6-line files | Whether a locally-run `dotnet run` can leave state that silently ends up inside a container image | Empirically verified for both services: planted a fake stale `.db` file in each service's folder, rebuilt, and confirmed via `docker run --entrypoint sh <image> -c "ls /app/*.db"` that no `.db` file exists in either built image |
| `README.md` | Trivial — prose only, no functional risk | Whether the documented commands and ports actually match what's configured | Cross-checked against the actual `appsettings.json`/`launchSettings.json` values and the working `docker-compose.yml`; the Docker Compose commands in this section were literally run as part of Phase 4 verification |

## Test Coverage

### Planned vs Actual

This story's plan documented a deliberate decision: no new automated
test file, since there's no C# code here for `dotnet test` to exercise.
Verification is manual, recorded inline in
[docs/plans/8_docker-compose-plan.md](../plans/8_docker-compose-plan.md)'s
Phase 4.

| Planned Test | Status | Notes |
|---|---|---|
| `docker compose up --build` brings both services to a healthy state with no manual steps (DEP-4) | verified | `docker compose ps` showed both `healthy`; build log confirmed the Account Service was logged healthy *before* the Gateway container even started, proving `depends_on: condition: service_healthy` actually gated startup rather than both happening to finish independently |
| Gateway's Account Service base URL resolves via Compose service-name DNS, not a hardcoded value | verified | Full `POST /events` → `GET /events/{id}` → `GET /accounts/{accountId}/balance` flow succeeded end-to-end through both containers |
| Manual (non-Docker) startup instructions are accurate (DEP-5) | verified | Commands match `appsettings.json`/`launchSettings.json` exactly; the existing `GatewayToAccountServiceFullFlowTests.cs` integration suite already proves the two services can talk to each other in-process, and this story's manual Docker verification proves it again as genuinely separate processes |
| (unplanned) Port exposure is scoped to loopback only, not all network interfaces | added | Caught by `/workflow-review`'s security pass, not in the original plan; verified via `docker port` showing `127.0.0.1:5199->8081/tcp` after the fix, and a live `curl 127.0.0.1:...` still succeeding |
| (unplanned) Stale local `.db` files can't leak into a built image | added | Also caught by `/workflow-review`; verified by planting a fake `.db` file and confirming it's absent from the built image |

### What's Not Tested

Nothing in this diff has C# code for `dotnet test` to exercise, so the
full existing suite (92 tests across both services) is unaffected and
was re-run after every change on this branch to confirm no regression.
Three low-priority `/workflow-review` suggestions were left pending
(floating base-image tags, containers running as root, a redundant
`dotnet publish` restore pass) — recorded in
[docs/reviews/8_docker-compose.json](../reviews/8_docker-compose.json)
for a future pass rather than blocking this story, since none of them
affect DEP-4/DEP-5's actual acceptance criteria.
