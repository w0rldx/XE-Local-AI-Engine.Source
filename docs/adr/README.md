# Architecture Decision Records

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17

These records capture repository design decisions and their implementation context. An **Accepted**
status means the repository adopted the stated design; it is not evidence of operational
effectiveness, compliance, certification, formal risk acceptance, or continued production use.

| ADR | Status | Decision scope |
| --- | --- | --- |
| [0001 — Development Mode restart recovery uses replacement attempts](0001-development-mode-restart-recovery.md) | Accepted | Restart recovery preserves worktree/artifact state and continues through a new attempt rather than resuming an interrupted provider stream. |
| [0002 — Development cloud authorization uses `ChatOptions.AdditionalProperties`](0002-development-cloud-egress-carrier.md) | Accepted | Version-aware carrier and enforcement seam for Development Mode cloud authorization. |
| [0003 — Six-plan implementation scope and hardware evidence decisions](0003-six-plan-operator-decisions.md) | Accepted | Operator-supplied scope decisions, unavailable-hardware evidence handling, and embedding width. |
| [0004 — Docker permitted for Development Mode execution only, as a stopgap ahead of MXC](0004-development-mode-container-execution-docker-stopgap.md) | Accepted | Narrows the runtime-rearchitecture epic's "no Docker anywhere" decision to "no Docker on the inference path" and unblocks its container-execution slices. |
| [0005 — Training runs in a uv-managed Python runtime, holds the node exclusively, and lands in a thin provider project](0005-training-runtime-python-exclusivity-and-project-placement.md) | Accepted | Training semantics live in Python behind a structured stdio contract; a run holds a training marker plus the runtime-mutation lease (never the GPU load-admission semaphore); a thin `Providers.Training` project owns only uv/venv/subprocess mechanics. |
| [0006 — Agentic MCP keys capture bounded operator-equivalent execution authority](0006-agentic-trust-mcp-key-scopes-and-auto-approval.md) | Accepted | Explicit inbound authority, durable capture across restart and rotation, agentic-root tool adaptation, and strict audit-before-invocation without granting the Operator role. |
| [0007 — The sandbox execution substrate is capability-declared, and the backend is selected, never named](0007-sandbox-execution-substrate-and-backend-selection.md) | **Accepted** | A consumer declares execution requirements rather than naming a backend; a selector resolves one that can honour them and fails closed when none can. Amends ADR 0004 Decision §1 only; §2–§5 stand. |
| [0008 — External integrations invoke a saved agent through a keyed, loopback-only surface inside `/api/local/v1`](0008-external-integrations.md) | **Accepted** | An external caller invokes a saved agent through hand-mapped `integration-api/…` routes inside `/api/local/v1` with their own `xeint_` keys; admission is one `BEGIN IMMEDIATE` transaction bounded per node and per principal, runs are unattended and fail closed, and V1 is explicitly loopback-only. |

For the baseline technical/security narrative and its explicit evidence limitations, see the
[Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md).
