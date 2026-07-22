# XE Local AI Engine — Developer Wiki

> Last reviewed: 2026-07-22 · Code-grounded.

XE Local AI Engine (product name **XE AI-Engine**) is the **node-side runtime** of the C0re platform. A single in-process
**Node Web Server** (`XE-Local-AI-Engine.Client`) serves the React management UI, owns the one
outbound platform link (`WorkerHub`), exposes loopback-only local APIs (`/api/local/v1`) plus
SignalR hubs, persists chat/agent state in **encrypted SQLite**, and runs the local model runtime
**in-process** by supervising a host **llama.cpp** (`llama-server`) child process.

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
worktree; see [Architecture Overview](01-architecture-overview.md) for the source/worktree flow and
[Security & Privacy](12-security-and-privacy.md) for the host-user execution boundary.

## Page index

| # | Page | Covers |
| --- | --- | --- |
| 01 | [Architecture Overview](01-architecture-overview.md) | System boundary, layering, dataflow, Development Mode source/worktree flow, the architecture invariants |
| 02 | [Project Layout](02-project-layout.md) | Every `.csproj`, dependency graph, central package mgmt, analyzer wall |
| 03 | [Local Runtime & Providers](03-local-runtime-and-providers.md) | llama.cpp supervisor, GPU variant select, binary updater, provider seams (LlamaServer/Ollama/HuggingFace/CodexOAuth/Capabilities) |
| 04 | [Agent Mode](04-agent-mode.md) | MAF wiring, tool registries, AgentHome, Playbook P1–P5, Memory, Capacity, Coder, sub-agent spawn |
| 05 | [Chat](05-chat.md) | `RuntimeChatClient` per-send routing, ordered parts, sampling, attribution, at-rest encryption |
| 06 | [Scheduler](06-scheduler.md) | Quartz.NET jobs, run history, cancellation, live hub, encoded gotchas |
| 07 | [Model-fit / Advisor](07-model-fit.md) | Cache-read vs scheduler refresh, `MemoryFitEstimator`, hardware profiler, sanitization |
| 08 | [Data & Persistence](08-data-and-persistence.md) | EF Core + SQLite, per-column AEAD encryption, entities, migration timeline |
| 09 | [API & Hubs](09-api-and-hubs.md) | FastEndpoints `/api/local/v1`, SignalR hubs, WorkerHub, OpenAPI→hey-api |
| 10 | [React Client](10-react-client.md) | 17 features, TanStack Query/Zustand, hey-api, dialog system, i18n, SPA serving |
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
