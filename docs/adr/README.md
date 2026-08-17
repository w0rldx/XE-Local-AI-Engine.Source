# Architecture Decision Records

> Baseline: `ebffe10ee4d9343d39be0b24bedb479c5a848dfd` · Reviewed: 2026-08-17

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

For the baseline technical/security narrative and its explicit evidence limitations, see the
[Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md).
