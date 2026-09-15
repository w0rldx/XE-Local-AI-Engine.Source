# Audits and point-in-time reports

Every document in this folder is a **point-in-time record**: it describes the repository as it stood on
the date in its file name, against the commit each one names. Findings may since have been implemented,
superseded or overtaken. Nothing here is maintained as current.

**For how the system works today, read [`docs/wiki/`](../wiki/Home.md)**, starting at
[Home](../wiki/Home.md). For the rules that came out of past work, read
[`docs/agent-knowledge.md`](../agent-knowledge.md).

| Date | Document | Subject | Status |
|---|---|---|---|
| 2026-07-26 | [2026-07-26-model-role-audit.md](2026-07-26-model-role-audit.md) | `ModelRole` enumeration and scope audit | Historical snapshot |
| 2026-07-28 | [technical-security-architecture/](technical-security-architecture/README.md) | Technical and Security Architecture Dossier — six chapters covering trust boundaries, sensitive assets, threat scenarios, operations, supply chain and claim traceability | Historical snapshot |
| 2026-07-31 | [2026-07-31-live-evaluation-lane-merge.md](2026-07-31-live-evaluation-lane-merge.md) | Live evaluation lane merge | Historical snapshot |
| 2026-08-07 | [2026-08-07-ai-inference-stack-performance-audit.md](2026-08-07-ai-inference-stack-performance-audit.md) | AI/inference stack performance and efficiency audit | Historical snapshot |
| 2026-08-07 | [2026-08-07-ai-inference-stack-performance-audit-v2.md](2026-08-07-ai-inference-stack-performance-audit-v2.md) | AI/inference stack performance audit v2 — verification and extension | Historical snapshot |
| 2026-08-07 | [2026-08-07-open-source-readiness-review.md](2026-08-07-open-source-readiness-review.md) | Open-source release readiness and compatibility review | Historical snapshot |
| 2026-08-07 | [2026-08-07-streaming-budget-redesign.md](2026-08-07-streaming-budget-redesign.md) | Streaming budget redesign — delta-only protocol, O(delta) persistence, bounded streams, disconnect grace | Historical snapshot; the design it records shipped |
| 2026-08-08 | [2026-08-08-documentation-disposition.md](2026-08-08-documentation-disposition.md) | Documentation disposition audit — per-file classification and disposition | Historical snapshot |
| 2026-08-12 | [2026-08-12-final-inference-optimization-audit.md](2026-08-12-final-inference-optimization-audit.md) | Final local-inference optimization audit | Historical snapshot |
| 2026-08-13 | [2026-08-13-agent-harness-architecture-audit.md](2026-08-13-agent-harness-architecture-audit.md) | Agent-harness architecture audit | Historical snapshot |
| 2026-08-22 | [2026-08-22-llama-server-process-supervisor-decomposition.md](2026-08-22-llama-server-process-supervisor-decomposition.md) | `LlamaServerProcessSupervisor` decomposition report | Historical snapshot |

The dossier chapters are numbered and meant to be read in order; its own
[README](technical-security-architecture/README.md) states the frozen baseline, the evidence states it
uses, and what it is explicitly not.
