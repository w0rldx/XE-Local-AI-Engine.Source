# ADR 0001: Development Mode restart recovery uses replacement attempts

- **Status:** Accepted
- **Date:** 2026-07-21
- **Scope:** Development Mode restart recovery

## Context

An active coder or reviewer may be interrupted before output, during streaming or a read, after a workspace write, or after validation evidence is persisted. Provider streams and arbitrary command execution cannot be resumed safely or replayed without risking duplicate side effects.

The recovery design retains five primary concepts: project, task, attempt, artifact, and event. It uses an explicitly trusted repository and code-owned commands because the Process provider is not an operating-system isolation boundary.

## Decision

1. Mark every attempt found `Running` at restart as `Interrupted` exactly once.
2. Continue only through a new attempt whose predecessor points to the interrupted attempt; never resume the old provider stream.
3. Preserve the Git worktree, its diff, and every persisted artifact. A replacement independently inspects the current worktree.
4. Bind validation and review evidence to the base commit, workspace subject hash, and changed-files manifest hash.
5. When the base commit is unchanged but the subject or manifest differs, invalidate stale validation and review evidence and permit replacement against the newly inspected workspace.
6. When the base commit differs from persisted validation or review evidence, block the task as unreconciled and do not create a replacement.
7. Recovery and replacement creation may inspect Git state but must not replay a coordinator write or validation command. Missing command-result evidence remains missing rather than being fabricated.
8. The protected `main` branch remains unchanged; all mutation is isolated to the temporary Development worktree branch.

## Evidence

`DevelopmentRestartRecoveryTests` exercises these forced interruption boundaries against a temporary Git repository and worktree:

- before first token;
- mid-stream;
- during a read tool;
- after a file write but before tool-result persistence;
- after validation artifact persistence but before attempt terminalization.

Additional cases prove exact five-concept scope, restart idempotency, preservation of non-running statuses, subject/manifest stale-evidence invalidation, base-mutation blocking, predecessor linkage, no command replay, and no protected-branch mutation.

## Consequences

- Restart-by-replacement does not require a workspace journal beyond hash-bound artifacts.
- A same-base workspace mutation is deterministic to inspect, but it invalidates prior validation and review evidence.
- A moved or otherwise unreconciled base stops progress and requires operator resolution.
- The focused recovery harness remains test-only; production recovery uses the durable SQLite store and startup reconciler.
- Cloud transport authorization is an independent boundary described by ADR 0002.
