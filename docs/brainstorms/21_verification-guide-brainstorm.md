# Brainstorm: Deployment & requirements verification guide

**Date:** 2026-07-16
**Issue:** #21

## Problem Statement

An evaluator reviewing this submission may want to exercise the running
system directly — deploy it via Docker, then confirm each of the
assignment's numbered requirements actually works — rather than only
reading source code or test files. This story adds a standalone,
copy-pasteable guide covering deployment plus a per-requirement
verification walkthrough with concrete test data, without duplicating
what README.md already documents (Docker/manual startup commands, the
test-coverage table) or overlapping issue #10's scope (finishing
README's own remaining `TODO` stubs).

## Codebase Context

- **`docker-compose.yml`** (confirmed current, post-security-fix):
  `gateway` on `127.0.0.1:5099:8080`, `account-service` on
  `127.0.0.1:5199:8081`, `depends_on: account-service: condition: service_healthy`,
  both healthchecks status-code-only (`curl -f`, 5s interval, 3s timeout,
  5 retries, 10s start period). No volumes — ephemeral SQLite.
- **`README.md`** already fully documents `docker compose up/down`, both
  `/health` curls, and a TEST-1..8 → test-file coverage table. The new
  guide must cross-reference these, not restate them — its value-add is
  the deeper per-requirement walkthrough README doesn't contain.
- **Exact endpoint shapes** (confirmed by reading the controllers
  directly, not assumed):
  - `POST /events` — `{eventId, accountId, type, amount, currency, eventTimestamp, metadata?}` → `201` (new) / `200` (duplicate) / `400` (validation) / `503` (Account Service unreachable).
  - `GET /events/{eventId}` → `200` or `404`.
  - `GET /events?account={accountId}` → `200`, array, empty if none.
  - `GET /accounts/{accountId}/balance` (Gateway passthrough) → verbatim-forwarded `{accountId, balance}`, or `503` with `account_service_unavailable`.
  - `GET /health` (both services) → always `200`, `{status, database}`.
  - Account Service's own `POST /accounts/{accountId}/transactions`,
    `GET /accounts/{accountId}/balance` (never `404`, unknown accounts
    return `balance: 0`), `GET /accounts/{accountId}`.
- **`standards/api.md`**'s validation rules: `EventValidator.cs` checks 6
  independent conditions (not 4 as initially assumed) — `eventId`,
  `accountId`, `type` (exactly `CREDIT`/`DEBIT`), `amount` (required,
  `> 0`), `currency`, `eventTimestamp` (valid ISO 8601), plus `metadata`
  must be an object if present. Exact error message strings confirmed
  for use verbatim in the guide.
- **`architecture/resiliency.md`**'s exact thresholds: 2s per-attempt
  timeout, 2 retries (3 attempts total) at a fixed 200ms delay, circuit
  breaker at ≥50% failure ratio over a 10s window with a 4-call minimum
  throughput, 5s break duration. Pipeline order: circuit breaker (outer)
  → timeout → retry (inner). Worst-case hung-call latency ≈6.4s.
- **Trace ID logging**: `Serilog.Formatting.Json.JsonFormatter` nests
  custom `LogContext` properties (including `TraceId`, pushed by
  `TraceLoggingMiddleware.cs` in both services) under a `Properties`
  object in each JSON log line — not flattened to the top level. The
  guide's log-grep command needs to account for this nesting.
- **Observability gap**: the system records a custom metric (a request
  Counter via OpenTelemetry) but registers no exporter — no Prometheus
  endpoint, nothing queryable over HTTP, a deliberate scope decision
  per `architecture/observability.md`. There is no curl command that can
  demonstrate this requirement directly.
- **No existing curl example bodies** exist anywhere in `docs/` — issue
  #8's manual verification (`docs/plans/8_docker-compose-plan.md` Phase
  4) confirmed the pattern (`curl -X POST .../events` with an unspecified
  payload) but never recorded exact JSON, so this guide's test data is
  new authorship, not reuse.
- **File placement**: no existing `docs/` subdirectory fits a
  user-facing guide (`brainstorms/`, `plans/`, `handoffs/`, `reviews/`
  are all issue-anchored Claude workflow artifacts; `architecture/`/
  `standards/` are settled-decision docs). A new top-level `docs/` file
  matches the existing pattern of `docs/CLAUDE.md`/`docs/simplify-patterns.md`.

## Q&A Decisions

**Q1: File name and location?**
A: `docs/verification-guide.md` — a new top-level file, matching the existing pattern of non-issue-anchored `docs/` files.

**Q2: How should the guide handle the Observability "custom metric" requirement, given no exposed metrics endpoint exists to curl?**
A: Explain the gap honestly — state the metric is recorded in-process but not exposed via any endpoint (a deliberate design decision, not an oversight), and point to `RequestMetricsMiddlewareTests.cs` (which uses a `MeterListener` to observe real recorded values) as the actual proof, consistent with how the guide treats anything a curl command can't directly observe.

**Q3: Should the guide include a quick-reference cheat sheet in addition to the detailed narrative walkthrough?**
A: Yes — a short endpoint/one-liner table at the top, followed by the full per-requirement narrative with concrete test data.

**Q4: Should the circuit-breaker demo be a full timed exercise or a looser description?**
A: Full timed exercise — exact commands, an explicit "wait 5 seconds" step, and the expected response shape at each stage. It's the single most distinctive resiliency behavior in the system and worth the ~30 seconds it takes to actually observe.

## Proposed Approaches

### Approach 1: Single comprehensive guide (Recommended)

One new file, `docs/verification-guide.md`, structured as: a short
prerequisites/deploy section (cross-referencing README's Docker
instructions rather than repeating them), a quick-reference cheat-sheet
table, then a per-requirement-section narrative walkthrough (Core
Functionality's four sub-behaviors, Service Separation, Distributed
Tracing, Observability, Resiliency including the full timed
circuit-breaker exercise, Graceful Degradation) each with concrete,
copy-pasteable `curl` commands and real JSON test data. Linked once from
README, not duplicated into it.

**Pros:**
- Matches the issue's acceptance criteria exactly: one file, cross-
  referenced from README, no restatement of README's own content.
- A single narrative reads naturally top-to-bottom — deploy, then verify
  — matching how an evaluator would actually use it in one sitting.
- Reuses exact, source-verified endpoint shapes and thresholds gathered
  in this brainstorm's research, so every command is accurate on first
  read rather than needing revision after a failed copy-paste.

**Cons:**
- A single file covering 6 requirement sections plus deployment will run
  fairly long — acceptable for a reference document meant to be
  navigated by section headers, not read linearly every time.

### Approach 2: Split into two files (deployment quickstart + requirements verification)

**Pros:**
- Each file stays shorter.

**Cons:**
- Deploy-then-verify is one sequential narrative for the same audience,
  not two independent use cases — splitting adds a navigation hop
  (which file has what) for no real audience-segmentation benefit, and
  doubles the "where does this belong" decision this brainstorm just
  resolved for one file.

### Approach 3: Fold into README.md directly

**Pros:**
- One less file to maintain.

**Cons:**
- Directly contradicts the issue's own explicit acceptance criteria
  ("linked from README.md... without restating its content there") and
  would overlap issue #10's separate, already-scoped README finalization
  work. Not a legitimate option.

## Recommendation

**Approach 1.** It's the only option that satisfies the issue's explicit
acceptance criteria without overlapping #10, and all four Q&A decisions
point the same direction: one well-organized file, cross-referencing
rather than duplicating README, honest about what can and can't be
curl-demonstrated.

## Related Docs

- [README.md](../../README.md) — "Running the services" and "Running the tests" sections, cross-referenced not duplicated.
- [architecture/resiliency.md](../../architecture/resiliency.md) — exact thresholds used in the circuit-breaker demo.
- [standards/api.md](../../standards/api.md) — exact validation rules and error envelope.
- [docs/plans/8_docker-compose-plan.md](../plans/8_docker-compose-plan.md) — prior manual-verification precedent (pattern, not literal content, reused).
- [docs/CLAUDE.md](../CLAUDE.md) — `docs/` folder structure and governance.
