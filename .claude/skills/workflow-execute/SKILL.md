---
name: workflow-execute
description: Execute an implementation plan step by step with TDD and incremental commits, for a GitHub issue. Use when the user has a plan ready and wants to begin implementation on a feature branch.
disable-model-invocation: true
argument-hint: "[issue-number | plan file path]"
---

# Execute

You are systematically executing an engineering plan. Your job is to turn
a plan with checkboxes into working code, committed incrementally with
tests on a feature branch. Scaled down from a multi-repo/ADO-backed
reference: single repo, plans always live under `docs/plans/`, and
tracking is a GitHub issue, not a rich work-item state machine.

## Step 1: Load the Plan

1. If `$ARGUMENTS` is a file path, read that file.
2. If `$ARGUMENTS` is an issue number, search `docs/plans/` for a plan whose `**Issue:**` line references it.
3. If `$ARGUMENTS` is empty, find the most recent plan in `docs/plans/` by sorting on the `YYYY-MM-DD` date prefix and read it.
4. Parse the plan's checkbox items (`- [ ]`) as the work to be done.

## Step 2: Prepare the Branch and the Issue

**This step is MANDATORY — complete every sub-step before moving to Step 3.**

> **Preflight — settings.json pollution check:** Before branching, run `git diff .claude/settings.json`. If there are uncommitted changes (session-local permission grants auto-appended by the Claude Code permission system), revert them now: `git checkout HEAD -- .claude/settings.json`. Unreverted grants will pollute the diff with unintended permission entries.

### 2a: Resolve the issue

If the plan's `**Issue:**` line has a number, use it. Otherwise ask the user via `AskUserQuestion`: "No issue number found. Implementation should be tracked against a GitHub issue." — options: "Provide an issue number", "Continue without an issue".

### 2b: Ensure you are on the correct feature branch

1. Check the current branch name (`git branch --show-current`).
2. If it already contains the issue number (e.g. `3_service-separation`), stay on it.
3. If you are on a **different** feature branch or `master`, create one: `git checkout -b <issue-number>_<slug>` — matching the same issue-anchored convention as `workflow-brainstorm`/`workflow-plan`'s document filenames, so the branch, its brainstorm, and its plan all share one identifier. `<slug>` is a short kebab-case description matching the issue title. No `feat`/`fix`/`docs` type prefix — the issue itself (its title, its milestone) already carries that distinction; a prefix would just be a second place for it to go stale.
4. Do not commit directly to `master` for any story that has its own issue — branch first, always.

### 2c: Verify assignment

If the issue's `assignees` is empty, run `gh issue edit <N> --add-assignee "@me"`.

### 2d: Post the implementation-started comment

```
gh issue comment <N> --body "Implementation started. Plan: <plan filename from Step 1>. Tasks: <N checkbox items>. Branch: <branch name>."
```

## Step 3: Break Down into Tasks

1. Create a task for each logical unit of work from the plan's checkboxes using `TaskCreate`.
2. Set up dependencies between tasks where order matters (using `addBlockedBy`).
3. Announce the task list so the user can see what will be executed.

## Step 3.5: Architecture Pre-flight (mandatory for any plan touching `src/`)

Before writing a single line of implementation code, get binding layer rules for this work. Skip this step only if every plan checkbox is a docs, config, or test-only change with no production code under `src/`.

1. Derive a one-sentence description of what the plan implements and which service(s) it touches (e.g. "add the idempotent insert-or-fetch path in EventLedger.Gateway's EventRepository").
2. Invoke the `architecture-guide` skill with that description.
3. Extract the returned guidance — which folder each type belongs in (per [standards/backend-architecture.md](../../../standards/backend-architecture.md)), any conflict with a recorded decision.
4. **Treat these rules as non-negotiable constraints for every task in Step 4.**
5. If a rule conflicts with the plan, surface the conflict to the user via `AskUserQuestion` — options: "Update the plan to comply", "Continue as planned (accepted deviation)". Do not write code until resolved.

## Step 4: Execute Each Task

For each task, follow the red-green-refactor TDD cycle:

1. Mark the task as `in_progress`.
2. **Follow existing patterns.** Before writing new code, read related files to understand conventions, naming, directory structure, and idioms already established (`architecture/`, `standards/`, and any code already written for earlier stories). Match them.
3. **Red: Write failing tests first.** Write the test(s) specified in the plan's Testing Strategy for this task. Run them and confirm they fail. If they pass without implementation, the tests are not asserting the right thing — fix them.
4. **Green: Write the minimum implementation.** Just enough code to make the failing tests pass. Run tests and confirm they pass.
5. **Refactor: Clean up while keeping tests green.** Remove duplication, improve naming, simplify logic — without changing behavior. Run tests again to confirm they still pass.
6. **Commit incrementally.** Dispatch the `commit` skill with the exact files this task touched, plus commit-message guidance (a suggested `<type>(<scope>): <summary>` and the *why*, not just the *what*) for the `committer` agent to compose the final message from — the agent writes the actual text itself per its own procedure, and stages only those named files, refusing on an apparent secret.
7. **Check off the plan item.** Edit the plan file to change `- [ ]` to `- [x]` for the completed item.
8. Mark the task as `completed`.

### Integration tests

For any task touching the Gateway→Account Service call, use two `WebApplicationFactory` instances wired together (Gateway's `HttpClient` pointed at the Account Service factory's client) rather than spinning up real Kestrel processes — see the integration-test approach already agreed in this project's design pass. A task that adds or changes this call is NOT complete until the integration test passes against both real (in-process) services, not a mocked Account Service.

### Renames and refactors

**Cross-cutting renames** (e.g. renaming a type or property that appears in many files): before using `Edit` with `replace_all: true`, run `Grep` for the identifier first. If matches include references to a third-party SDK/framework type (e.g. `HttpClient.Timeout`, EF Core's own members), fully-qualified usages, or documentation/plan/review artifacts that reference the old name historically — prefer per-file edits with explicit surrounding context, or scope-limited `replace_all` per file. The safe pattern is **Grep → review matches → narrow edits**. A bare `replace_all` over a short identifier almost always hits framework names and docs in the same pass.

### ASP.NET Core project conventions

When executing tasks that add infrastructure wiring to either service:

- **`Program.cs` is an orchestrator only.** DI registration and middleware setup belong in focused extension methods in `Infrastructure/` (e.g. `AddGatewayInfrastructure`, `AddAccountServiceInfrastructure`), per [standards/backend-architecture.md](../../../standards/backend-architecture.md). Do not add lines directly to `Program.cs`. Pass `WebApplicationBuilder` (not `IServiceCollection` alone) to registration methods that need configuration, e.g. the Account Service base URL or the resilience pipeline settings.
- **A partial `Program` marker must exist** at each service's project root if using top-level statements (`public partial class Program { }` at file scope, no enclosing namespace) — this exposes `Program` to `WebApplicationFactory<Program>` in integration tests. Top-level statements already generate `Program` in the global namespace; a `partial` declaration inside any explicit namespace creates a different, unrelated type. Add this before writing the cross-service integration test (Story 8), not after discovering `WebApplicationFactory<Program>` doesn't compile.
- **DI wiring classes have no business logic.** Apply `[ExcludeFromCodeCoverage]` at the class level on `ServiceCollectionExtensions` / one-liner middleware registration extensions. Do not exclude controller classes — they have dispatching and mapping logic that must be tested.

### Simplify-patterns awareness

Before starting implementation, check if `docs/simplify-patterns.md` exists. If it does, read it and apply its lessons while coding — these are recurring issues `/simplify` (Step 6 below) has fixed in past executions on this repo; avoiding them upfront produces cleaner first-pass code. On the first story, this file won't exist yet — that's expected, not an error.

### Guidelines during execution

- Prefer small, focused changes over large sweeping ones.
- Do not refactor or "improve" code outside the scope of the plan.
- If you encounter an ambiguity or decision point not covered by the plan, use `AskUserQuestion` to clarify before proceeding.
- If the test project doesn't exist yet, set it up as the first task (xUnit, per [standards/backend-architecture.md](../../../standards/backend-architecture.md#test-project-layout)) before proceeding with implementation.

## Step 5: Final Checks

After all tasks are complete:

1. Run the full test suite via the `test-dotnet` skill. Fix any failures.
2. Run `dotnet format --verify-no-changes --no-restore` and confirm exit code 0. (The `.claude/scripts/format-file.sh` PostToolUse hook already formats on every `Write`/`Edit`, so this should normally be a no-op check, not a source of new diffs.)
3. Verify all plan checkboxes are checked off.
4. **Index consistency check.** If any new `.md` was added under `standards/`, `docs/solutions/`, or `architecture/`, grep for its basename in the parent directory's `README.md` (if present) and in `CLAUDE.md`'s skill/doc pointer tables. A new doc that nothing links to is invisible to both readers and the `architecture-advisor` agent — add it to the relevant index before moving on.
5. Verify test coverage: ensure every implementation step from the plan has at least one corresponding test. Flag any gaps to the user.
6. **Post-commit settings.json cleanup:** run `git diff HEAD .claude/settings.json`. If it changed after the last commit (new permission grants auto-appended during execution), revert: `git checkout HEAD -- .claude/settings.json`.

## Step 6: Simplify

Run `/simplify` to review all changed code for reuse opportunities, quality issues, and unnecessary complexity. Fix any issues found before reporting completion. `/simplify` refines all recently-changed code — production and tests alike — scoped to this work's diff.

### Test-suite re-run gate (required for non-trivial changes)

`/simplify` can ship reverts — a simplification that looks safe but changes behavior. Apply this gate **before** committing any non-trivial `/simplify` change:

**Trigger criteria:** a rename touching ≥3 callers; dedupe across files; a signature change on a method whose test mocks its dependencies. Trivial changes (formatting, dead-code deletion confirmed by grep) skip the gate.

**Procedure:** apply the change → run the relevant test scope (the modified files' tests, not the full suite) → if any test fails, revert the specific suggestion and do not commit; append a `❌ Rejected:` entry to `docs/simplify-patterns.md` (format below) so `/simplify` doesn't re-propose it → if tests pass, commit.

```markdown
- ❌ **Rejected: <short name>**: <what was tried>. Failed because: <reason>. *(First seen: YYYY-MM-DD)*
```

### Capture accepted patterns

After `/simplify` finishes, review what it accepted. If any fixed a recurring or notable pattern, append a concise entry to `docs/simplify-patterns.md`:

```markdown
# Simplify Patterns

Recurring code quality issues identified by /simplify across executions.
Read this before implementing to avoid repeating past mistakes.

## Patterns

- **<Short pattern name>**: <One-sentence description>. *(First seen: YYYY-MM-DD)*
```

If `/simplify` made no meaningful changes, skip this step.

## Step 7: Report Completion

1. `gh issue comment <N> --body "Implementation complete. <N> tasks done. Branch: <branch name>. Ready for /workflow-handoff <N>."`
2. Summarize what was executed, note the feature branch name, and suggest running `workflow-handoff` next.
