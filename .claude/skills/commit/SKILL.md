---
name: commit
description: Create a git commit for a specific, already-reviewed set of files. Use when the user asks to commit changes.
allowed-tools: Task
---

This skill is a thin dispatcher to the `committer` agent (see
[.claude/agents/committer.md](../../agents/committer.md)), which owns the
actual staging/committing procedure, secret-scanning, and the
no-broad-add / no-amend rules.

## Steps

1. Determine the exact set of file paths the user wants committed. If the
   user said "commit my changes" without naming files, ask which files —
   do not assume "everything currently modified."
2. Dispatch a `Task` to the `committer` agent with that explicit file
   list and any commit-message guidance the user gave.
3. Return the agent's report (files committed, message used, commit hash,
   or why it stopped) to the user.

Do not stage or commit anything directly within this skill — that
responsibility belongs entirely to the `committer` agent, including its
secret-scan and hook-failure handling.
