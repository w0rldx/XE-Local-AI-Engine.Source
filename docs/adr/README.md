# Architecture Decision Records

> Baseline: `7e64ed589e14eecc0e522e807d2e531a1095d19a` · Reviewed: 2026-07-28

These records capture repository design decisions and their implementation context. An **Accepted**
status means the repository adopted the stated design; it is not evidence of operational
effectiveness, compliance, certification, formal risk acceptance, or continued production use.

| ADR | Status | Decision scope |
| --- | --- | --- |
| [0001 — Development Mode restart recovery uses replacement attempts](0001-development-mode-restart-recovery.md) | Accepted | Restart recovery preserves worktree/artifact state and continues through a new attempt rather than resuming an interrupted provider stream. |
| [0002 — Development cloud authorization uses `ChatOptions.AdditionalProperties`](0002-development-cloud-egress-carrier.md) | Accepted | Version-aware carrier and enforcement seam for Development Mode cloud authorization. |
| [0003 — Six-plan implementation scope and hardware evidence decisions](0003-six-plan-operator-decisions.md) | Accepted | Operator-supplied scope decisions, unavailable-hardware evidence handling, and embedding width. |

For the baseline technical/security narrative and its explicit evidence limitations, see the
[Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md).
