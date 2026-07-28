# XE Local AI Engine — Developer Wiki

> Baseline: `7e64ed589e14eecc0e522e807d2e531a1095d19a` · Reviewed: 2026-07-28 · Code-grounded.

XE Local AI Engine (product name **XE AI-Engine**) is the **node-side runtime** of the C0re platform. A single
**Node Web Server process** (`XE-Local-AI-Engine.Client`) serves the React management UI, owns the one
outbound platform link (`WorkerHub`), exposes loopback-only local APIs (`/api/local/v1`) plus
SignalR hubs, persists selected sensitive payloads in SQLite with **per-column AEAD encryption**, and runs the local model runtimes
as node-owned, supervised host child processes — **llama.cpp** (`llama-server`) for text, and
**stable-diffusion.cpp** (`sd-server`) for images.

This wiki is the contributor deep-dive for the current codebase. It supersedes the older
`docs/ai-runtime.md` notes where they conflict (those predate the runtime re-architecture).

## Start here

- **New to the repo?** Read [Architecture Overview](01-architecture-overview.md) then
  [Project Layout](02-project-layout.md).
- **Touching inference / models?** [Local Runtime & Providers](03-local-runtime-and-providers.md)
  and [Model-fit / Advisor](07-model-fit.md).
- **Touching agents / chat?** [Agent Mode](04-agent-mode.md) and [Chat](05-chat.md).
- **Touching data?** [Data & Persistence](08-data-and-persistence.md).
- **Shipping / running it?** [Hosting & Deployment](11-hosting-and-deployment.md).
- **Reviewing architecture or security without source access?** Use the
  [Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md).
  It is a baseline description with evidence limitations, not an assurance or compliance package.

## The one fact that changed everything

The **runtime re-architecture** (locked 2026-06-17) replaced the old container/HostAgent model:

| Was | Now |
| --- | --- |
| Docker / `Docker.DotNet` container sandbox for inference | **Host `llama-server` child process**, supervised in-process |
| `XE-Local-AI-Engine.HostAgent.*` connection layer | **Deleted** — host owns the runtime directly |
| Ollama as the default dev runtime | Ollama **present as a provider but de-orchestrated** from Aspire dev |
| Models pre-provisioned | **HuggingFace GGUF** discovery/download + box-aware **Model Advisor** |

If you find a doc, comment, or assumption that still describes Docker or HostAgent as live, it is
stale — trust the code and these pages.

**Shipped since the last review (2026-06-24…27):** a profile-driven **inference optimizer** (per-machine
explore → freeze → replay tuning; the supervisor no longer forces `--n-gpu-layers 999` — see
[Local Runtime & Providers](03-local-runtime-and-providers.md) and the [Architecture Overview](01-architecture-overview.md) launch-args seam),
**GGUF quant recommendation** (quality tier + hardware-fit + recommended-variant badge — see
[Model-fit / Advisor](07-model-fit.md)), **chat file upload → agent attachments** (encrypted store — see
[Chat](05-chat.md)), a browser **client voice runtime** (Kokoro TTS), and an **onboarding first-response tour**.

**Shipped flagship features (now documented):** local **image generation** via stable-diffusion.cpp (see
[Image Generation](14-image-generation.md)) and an offline **Knowledge Base / RAG** with hybrid FTS+vector
search and a local reranker (see [Knowledge Base / RAG](15-knowledge-base.md)). **Development Mode** is a
default-on local coding workflow that binds a registered Git source repository to an engine-owned detached
worktree — 16 `development/*` endpoints, a live-attempt hub, its own React feature, and two accepted Development Mode ADRs
([0001](../adr/0001-development-mode-restart-recovery.md), [0002](../adr/0002-development-cloud-egress-carrier.md));
see [Architecture Overview](01-architecture-overview.md#development-mode-registered-source-managed-worktree)
for the source/worktree flow and [Security & Privacy](12-security-and-privacy.md) for the host-user execution boundary.

**In-app llama.cpp source builds.** Upstream ships no prebuilt Linux CUDA `llama-server`, so the node can
compile one itself and adopt it as a managed runtime — an explicit, Operator-gated, prerequisite-checked
action, never implicit. Prebuilt download stays the default. If you read anywhere that this engine has
"no source build", that statement is wrong: see
[Local Runtime & Providers §2.6](03-local-runtime-and-providers.md#26-in-app-source-builds-linux).

## Page index

| # | Page | Covers |
| --- | --- | --- |
| 01 | [Architecture Overview](01-architecture-overview.md) | System boundary, layering, dataflow, Development Mode source/worktree flow, the architecture invariants |
| 02 | [Project Layout](02-project-layout.md) | Every `.csproj`, dependency graph, central package mgmt, analyzer wall |
| 03 | [Local Runtime & Providers](03-local-runtime-and-providers.md) | llama.cpp supervisor, GPU variant select, binary acquisition (prebuilt / BYO override / in-app source build), provider seams across all seven `Providers.*` projects (Abstractions, LlamaServer, HuggingFace, Ollama, CodexOAuth, Capabilities, StableDiffusionCpp) |
| 04 | [Agent Mode](04-agent-mode.md) | MAF wiring, tool registries, AgentHome, Playbook P1–P5, Memory, Capacity, Coder, sub-agent spawn |
| 05 | [Chat](05-chat.md) | `RuntimeChatClient` per-send routing, ordered parts, sampling, attribution, at-rest encryption |
| 06 | [Scheduler](06-scheduler.md) | Quartz.NET jobs, run history, cancellation, live hub, encoded gotchas |
| 07 | [Model-fit / Advisor](07-model-fit.md) | Cache-read vs scheduler refresh, `MemoryFitEstimator`, hardware profiler, sanitization |
| 08 | [Data & Persistence](08-data-and-persistence.md) | EF Core + SQLite, per-column AEAD encryption, entities, migration timeline |
| 09 | [API & Hubs](09-api-and-hubs.md) | FastEndpoints `/api/local/v1` (23 route families), 10 possible local SignalR hubs (9 unconditional plus conditional Development), WorkerHub, OpenAPI→hey-api |
| 10 | [React Client](10-react-client.md) | 25 features, TanStack Query/Zustand, hey-api, shared hub connections, dialog system, i18n, SPA serving |
| 11 | [Hosting & Deployment](11-hosting-and-deployment.md) | Aspire AppHost, desktop launcher, publish profiles, uninstaller |
| 12 | [Security & Privacy](12-security-and-privacy.md) | Egress boundary, secret handling, loopback/Host-Origin, redaction, node-local AI ops, Development Mode execution boundary |
| 13 | [Testing & Validation](13-testing-and-validation.md) | Test topology, validation commands, E2E, RC evidence |
| 14 | [Image Generation](14-image-generation.md) | Local stable-diffusion.cpp: `sd-server` supervisor, serialized job coordinator, encrypted image store |
| 15 | [Knowledge Base / RAG](15-knowledge-base.md) | Offline document KB: ingestion pipeline, hybrid FTS+vector search, reranker, agent tools |

## Conventions in this wiki

- Every structural claim cites source as `path/to/File.cs` (and symbol names where useful).
- Pages are dated `Last reviewed`. When you change a subsystem, update the matching page.
- Architecture invariants (egress, secrets, loopback, node-local privacy ops) are **rules**, not
  suggestions — see [Security & Privacy](12-security-and-privacy.md) and
  [Architecture Overview](01-architecture-overview.md).
