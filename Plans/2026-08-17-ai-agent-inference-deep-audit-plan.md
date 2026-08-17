# AI, Agent, and Inference Remediation Plan

**Date:** 2026-08-17
**Branch:** `audit/ai-agent-remediation-2026-08-17`
**Status:** Approved for implementation

## Objective

Resolve the confirmed lifecycle, trust-boundary, reproducibility, promotion, capacity, and validation gaps found by the 2026-08-17 read-only AI/agent/inference audit without weakening the repository's existing fail-closed approval, GPU-outcome, or runtime-mutation invariants.

## Confirmed findings

### Priority 1

1. A detached llama-server spawn can be published but never cleaned up when shutdown cancels task scheduling before delegate entry.
2. Conversation compaction erases role provenance and inserts generated recap text as a user message.
3. Non-interactive AI operations bypass common telemetry, deadlines, provider-call budgets, and error classification.
4. Evaluation reads mutable dataset rows instead of an immutable, identity-preserving run corpus.
5. Artifact promotion requires operational smoke success but no quality-evaluation decision.
6. Degraded non-NVIDIA capacity accounting can omit warm resident and external draft-model memory.
7. The training lifecycle has no tracked end-to-end validation.

### Priority 2

1. Failed approval delivery removes retry state and can leave the idle watchdog disabled.
2. One oversized message can bypass the summarizer's per-call input limit.
3. `spawn_subagent` JSON-schema string bounds are not enforced at the execution boundary.
4. Chat cancellation can race registration and cancellation-source disposal.
5. Capacity and supervisor model identities use different casing semantics.
6. Headless training/evaluation paths can persist raw exception messages.
7. Pending approval rehydration can render an uncategorized or empty tool card.

## Execution order

### Phase 1 — Regression locks

Add deterministic tests for each changed behavior before modifying production code. Prefer scheduler/barrier-controlled race tests over timing sleeps.

### Phase 2 — Lifecycle and trust-boundary corrections

- Make detached-spawn cleanup unconditional.
- Preserve or explicitly fence conversation-summary provenance.
- Keep approvals retryable until delivery succeeds and restore watchdog state on all exits.
- Coordinate cancellation callback and resource disposal lifetimes.
- Enforce sub-agent argument bounds before resolution or capacity work.
- Apply sanitized public errors to headless AI operations.

### Phase 3 — Shared non-interactive AI policies

Introduce the smallest existing-pattern-compatible construction point for deadlines, provider budgets, token/trace telemetry, and error translation. Function invocation remains opt-in.

### Phase 4 — Reproducible evaluation and promotion

- Version the frozen corpus format so it preserves stable sample identity.
- Evaluate from immutable run artifacts.
- Require an explicit successful evaluation/comparison decision for promotion, with an audited administrator override if product policy permits it.

### Phase 5 — Capacity correctness

- Use one normalized model identity across capacity and supervision.
- Account for resident and external draft-model footprints when live free-VRAM telemetry is unavailable.
- Fail closed when residency cannot be established reliably.

### Phase 6 — Validation

Run targeted tests first, then the load-bearing Release build/test gates. Run frontend, Python, OpenAPI, Aspire/Chrome, GPU, and tool-grammar validation when their affected surfaces require them. Add a tracked training lifecycle E2E path that refuses to pass when work is skipped.

## Required validation evidence

- Regression test proves each fixed transition or boundary.
- `dotnet restore XE-Local-AI-Engine.slnx`
- `dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore`
- `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1`
- Applicable frontend, Python, OpenAPI, release-script, GPU, grammar, and Aspire checks.
- Final diff review confirms no generated files were hand-edited and no unrelated cleanup was included.

## Stop conditions

Implementation is complete only when all approved findings are fixed or explicitly recorded as separately gated follow-up work, their tests pass, Release validation succeeds, and any unavailable hardware/live validation is reported as a concrete gap rather than a pass.
