# Deployment & requirements verification guide

**Issue:** #21

## Context

Builds on the brainstorm at
[docs/brainstorms/21_verification-guide-brainstorm.md](../brainstorms/21_verification-guide-brainstorm.md).
One new file, `docs/verification-guide.md`, walks an evaluator through
deploying via Docker and then verifying each of the assignment's
numbered requirements against the live, running system, with concrete
test data — cross-referencing README.md's existing Docker/test-coverage
content rather than restating it, and without overlapping issue #10's
separate README-finalization scope.

## Relevant Learnings

No `docs/solutions/` entries apply to this topic. The brainstorm's
research already confirmed every endpoint shape, validation rule, error
message, and resiliency threshold directly from source (not assumed) —
see the brainstorm's Codebase Context section for the full inventory.
One thing not yet confirmed: **no existing curl example bodies exist
anywhere in the repo** — issue #8's manual verification
(`docs/plans/8_docker-compose-plan.md` Phase 4) proved the pattern works
but never recorded exact JSON payloads. This means every command in the
new guide is fresh authorship that must be run against a real, live
`docker compose` deployment before being written down — a plausible-
looking but untested curl command in a document called "verification
guide" would be a direct contradiction of its own purpose.

## Implementation Steps

### Phase 1: Start the live verification environment

- [ ] Run `docker compose up --build -d` from repo root. Confirm both
  containers reach `healthy` via `docker compose ps` before proceeding —
  every subsequent phase's commands must be run against this real,
  running stack, not written from memory of expected behavior.

### Phase 2: Write the guide's opening — prerequisites, deploy, cheat sheet

- [x] Create `docs/verification-guide.md`. Opening section: one-line
  purpose statement, a link to README.md's "Running the services"
  section for the actual `docker compose up --build` walkthrough (do not
  repeat those commands here), and the base URLs
  (`http://localhost:5099` Gateway, `http://localhost:5199` Account
  Service).
- [x] Add the quick-reference cheat-sheet table: one row per endpoint
  (`POST /events`, `GET /events/{id}`, `GET /events?account=`,
  `GET /accounts/{accountId}/balance` on both the Gateway passthrough and
  the Account Service directly, `GET /health` on both services), each
  with a one-line `curl` example. Run each one-liner against the live
  stack from Phase 1 before writing it down.

### Phase 3: Core Functionality section (idempotency, out-of-order, balance, validation)

Use one continuous test-data narrative across this section (a single
`accountId` reused across idempotency/out-of-order/balance so the reader
follows one coherent story rather than disconnected throwaway examples).

- [x] Idempotency: `POST /events` with a fresh `eventId`, confirm `201`;
  `POST` the identical payload again, confirm `200` with the *same*
  `receivedAt` (not a fresh one) and unchanged balance via a follow-up
  balance check. Run live, capture actual response bodies for the guide.
- [x] Out-of-order: `POST` three events for the same account with
  `eventTimestamp`s deliberately submitted out of chronological order,
  then `GET /events?account=...` and confirm the array is sorted by
  `eventTimestamp` (not arrival order), then check the balance reflects
  the correct sum regardless of arrival order. Run live.
- [x] Balance: show a zero-transaction account (`GET .../balance` on a
  never-used `accountId`, expect `{"accountId":"...","balance":0}`, per
  the Account Service's confirmed "never 404" behavior) and the
  mixed-credit/debit result from the out-of-order step above. Run live.
- [x] Validation: one rejection example per rule in `EventValidator.cs`
  (6 total — missing `eventId`, missing `accountId`, invalid `type`,
  `amount <= 0`, missing `currency`, malformed `eventTimestamp`), each
  showing the exact `400` response body with its specific message
  string (sourced from `standards/api.md`/`EventValidator.cs` directly,
  confirmed in the brainstorm's research). Run each live to confirm the
  exact message text matches what's written.

### Phase 4: Service Separation, Distributed Tracing, Observability sections

- [ ] Service Separation: brief narrative (not a curl demo — this is a
  structural property, not runtime behavior) pointing to
  `docker-compose.yml`'s two independent `build:` contexts and
  `architecture/vertical-architecture.md`'s "no shared database" decision.
- [ ] Distributed Tracing: `POST /events`, capture the response, then
  `docker compose logs gateway` and `docker compose logs account-service`,
  grep both for the same trace ID. Confirm live the exact `jq`/`grep`
  command needed given `JsonFormatter` nests custom `LogContext`
  properties (including `TraceId`) under a `Properties` object, not at
  the top level — the brainstorm flagged this as needing a live check
  before finalizing the exact command syntax.
- [ ] Observability: cross-reference README's `/health` curl (already
  documented, don't repeat) for the structured-logging and health-check
  halves of this requirement. For the custom-metric half: state plainly
  that the metric (a request-count `Counter`) is recorded in-process but
  not exposed via any endpoint in this deployment — a deliberate design
  decision, not an oversight — and point to
  `tests/EventLedger.Gateway.Tests/RequestMetricsMiddlewareTests.cs` and
  its Account Service equivalent (both use a `MeterListener` to observe
  real recorded values) as the actual proof.

### Phase 5: Resiliency + Graceful Degradation sections

- [ ] Resiliency: the full timed circuit-breaker exercise. `docker compose stop account-service`,
  then fire 4+ `POST /events` requests in quick succession (enough to
  hit the circuit breaker's 4-call minimum throughput within its 10s
  sampling window), showing the first call(s) take ~2s (bounded by the
  per-attempt timeout, with retries) before returning `503`, and later
  calls return `503` near-instantly once the circuit opens (no network
  attempt). Explicit "wait 5 seconds" step (the break duration), then
  `docker compose start account-service` and one final `POST` showing
  recovery. Run this live end-to-end at least once to confirm the exact
  timing/response shapes before writing the guide's expected-output
  text — do not describe timing from the architecture doc's numbers
  alone without observing it actually happen.
- [ ] Graceful Degradation: with `account-service` still stopped (or
  stopped again), show `POST /events` → `503` and a follow-up
  `GET /events/{eventId}` for that same `eventId` → `404` (nothing
  persisted); `GET /events/{id}` and `GET /events?account=` for
  *already-existing* data → still `200` (local-only reads unaffected);
  `GET /accounts/{accountId}/balance` (Gateway passthrough) → `503`.
  Run live. `docker compose start account-service` afterward to leave
  the stack healthy again.

### Phase 6: Link from README, final verification, teardown

- [ ] Add exactly one line to `README.md` linking to
  `docs/verification-guide.md` (placed near the existing "Running the
  tests" section, since both are evaluator-facing verification content)
  — no other README changes, per the brainstorm's explicit "cross-
  reference, don't restate" decision.
- [ ] Full read-through of `docs/verification-guide.md` top to bottom,
  confirming every command's written expected-output text matches what
  was actually observed live during Phases 2–5 (not just "looks
  plausible").
- [ ] `docker compose down` — leave no stray containers running.

## Testing Strategy

### Test Environment

Not applicable in the usual xUnit sense — this plan adds no C# code.
"Testing" here means every command in the new guide is executed against
a real, live `docker compose up` deployment (Phase 1's environment)
before being written down, and the guide's written expected-output text
is a transcription of what was actually observed, not a prediction.

### Test Cases

- **Description**: Every cheat-sheet one-liner (Phase 2) returns the
  documented status/shape when actually run.
  **Type**: Manual, live. **Phase reference**: Phase 2.
- **Description**: The idempotency, out-of-order, balance, and all 6
  validation-rejection examples (Phase 3) produce exactly the response
  bodies written in the guide.
  **Type**: Manual, live. **Edge cases**: zero-transaction balance;
  each of the 6 distinct validation rules, not just a representative
  subset. **Phase reference**: Phase 3.
- **Description**: The trace-ID grep command (Phase 4) actually finds a
  matching `TraceId` in both services' logs for one real request, given
  `JsonFormatter`'s `Properties`-nested output shape.
  **Type**: Manual, live. **Phase reference**: Phase 4.
- **Description**: The circuit-breaker exercise (Phase 5) shows the
  documented timing/response progression when actually run once,
  end-to-end, not merely inferred from `architecture/resiliency.md`'s
  configured numbers.
  **Type**: Manual, live. **Phase reference**: Phase 5.
- **Description**: The graceful-degradation example (Phase 5) shows
  `POST /events` → `503`/nothing-persisted, both GET reads still
  working, and the balance passthrough → `503`, all against the same
  stopped-Account-Service state.
  **Type**: Manual, live. **Phase reference**: Phase 5.

## Decisions Made

- **One continuous test-data narrative for Core Functionality**, not
  disconnected per-example data — a single `accountId` carried across
  idempotency/out-of-order/balance so the section reads as one coherent
  story an evaluator can follow start to finish, matching how the guide
  itself is meant to be used in one sitting.
- **Every command is verified live before being written down**, not
  authored from the brainstorm's research alone. The research confirmed
  endpoint *shapes* and *thresholds* accurately, but exact response
  bodies (timestamps, generated IDs) and the trace-ID log-grep syntax
  (given `JsonFormatter`'s property nesting) can only be confirmed by
  actually running the commands.
- **`CancellationToken` propagation pattern**: not applicable — this
  plan adds no async C# call chains, only documentation and one README
  link.

### Known Constraints

- The Observability requirement's custom-metric half cannot be
  curl-demonstrated in this deployment by design (no exporter
  registered) — the guide states this honestly rather than working
  around it, per the brainstorm's Q2 decision.
