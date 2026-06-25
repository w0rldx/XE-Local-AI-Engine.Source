# XE Local AI Engine — Developer Wiki

> Last reviewed: 2026-06-24 · Code-grounded.

XE Local AI Engine is the **node-side runtime** of the C0re platform. A single in-process
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

## Page index

| # | Page | Covers |
| --- | --- | --- |
| 01 | [Architecture Overview](01-architecture-overview.md) | System boundary, layering, dataflow, the 10 architecture invariants |
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
| 12 | [Security & Privacy](12-security-and-privacy.md) | Egress boundary, secret handling, loopback/Host-Origin, redaction, node-local AI ops |
| 13 | [Testing & Validation](13-testing-and-validation.md) | Test topology, validation commands, E2E, RC evidence |

## Conventions in this wiki

- Every structural claim cites source as `path/to/File.cs` (and symbol names where useful).
- Pages are dated `Last reviewed`. When you change a subsystem, update the matching page.
- Architecture invariants (egress, secrets, loopback, node-local privacy ops) are **rules**, not
  suggestions — see [Security & Privacy](12-security-and-privacy.md) and
  [Architecture Overview](01-architecture-overview.md).
