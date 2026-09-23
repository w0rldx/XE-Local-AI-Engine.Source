# XE Local AI Engine — Developer Wiki

> Reviewed: 2026-09-15 · Code-grounded.

XE Local AI Engine (product name **XE AI-Engine**) is a **self-contained local AI node**. A single
**Node Web Server process** (`XE-Local-AI-Engine.Client`) serves the React management UI, exposes
loopback-only local APIs (`/api/local/v1`) plus SignalR hubs, persists selected sensitive payloads in SQLite with **per-column AEAD encryption**, and runs the local model runtimes
as node-owned, supervised host child processes — **llama.cpp** (`llama-server`) for text,
**stable-diffusion.cpp** (`sd-server`) for images and **whisper.cpp** (`whisper-server`) for speech-to-text.

This wiki is the contributor deep-dive for the current codebase. `docs/ai-runtime.md` is a companion, not a
predecessor: it holds the AI-seam maintenance rules and the current Microsoft.Extensions.AI / Microsoft Agent
Framework pins, while the wiki covers architecture.

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

## Architecture invariants

- Inference runs in **host child processes the node supervises itself** — `llama-server` for text, `sd-server`
  for images, `whisper-server` for speech-to-text. There is no container and no agent between the host and a
  model runtime.
- `XE-Local-AI-Engine.HostAgent.*` does not exist; the host owns the runtime directly.
- Docker appears in exactly two places, neither on the inference path: the **Development Mode container
  sandbox provider**, which is opt-in (`Development:Sandbox:Provider=docker`, unset in the shipped config) per
  [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md), and the engine-owned container
  runtime behind [External Apps](23-external-apps.md).
- Detail: [Architecture Overview](01-architecture-overview.md), [Local Runtime & Providers](03-local-runtime-and-providers.md),
  [Security & Privacy](12-security-and-privacy.md).

### Invariants worth knowing

- **The Open Canvas import runs automatically at startup and stops startup if it fails.** Saved canvases from the
  removed Open Canvas (Preview) builder are converted into Graph Workflow definitions on the first start of the
  build that removed it; the encrypted source is staged so an interrupted or failed conversion retries on the next
  start instead of being lost. Read [§9 of the Graph Workflows page](21-graph-workflows.md#9-the-open-canvas-import)
  before upgrading a node whose canvases matter.
- **A doc or comment claiming "no Docker anywhere" predates ADR 0004 and is stale**; one claiming Docker on the
  inference path is stale the other way. The bullet above is the current rule.

## Page index

| # | Page | Covers |
| --- | --- | --- |
| 01 | [Architecture Overview](01-architecture-overview.md) | System boundary, layering, dataflow, Development Mode source/worktree flow, the architecture invariants |
| 02 | [Project Layout](02-project-layout.md) | Every `.csproj`, dependency graph, central package mgmt, analyzer wall |
| 03 | [Local Runtime & Providers](03-local-runtime-and-providers.md) | llama.cpp supervisor, GPU variant select, binary acquisition (prebuilt / BYO override / in-app source build), provider seams across every `Providers.*` project registered in `XE-Local-AI-Engine.slnx` (Abstractions, LlamaServer, HuggingFace, Ollama, CodexOAuth, Capabilities, StableDiffusionCpp, Training) |
| 04 | [Agent Mode](04-agent-mode.md) | MAF wiring, tool registries, AgentHome, Playbook P1–P5, Memory, Capacity, Coder, sub-agent spawn |
| 05 | [Chat](05-chat.md) | `RuntimeChatClient` per-send routing, ordered parts, sampling, attribution, at-rest encryption |
| 06 | [Scheduler](06-scheduler.md) | Quartz.NET jobs, run history, cancellation, live hub, encoded gotchas |
| 07 | [Model-fit / Advisor](07-model-fit.md) | Cache-read vs scheduler refresh, `MemoryFitEstimator`, hardware profiler, sanitization |
| 08 | [Data & Persistence](08-data-and-persistence.md) | EF Core + SQLite, per-column AEAD encryption, entities, schema milestones |
| 09 | [API & Hubs](09-api-and-hubs.md) | FastEndpoints `/api/local/v1` (one route family per nested class in `LocalApiRoutes`), the local SignalR hubs registered by the `MapHub<>` block in `Client/Program.cs` (all unconditional except `DevelopmentAttemptHub`) — this page owns the hub list, operator-authored custom tools, the inbound MCP tool surface, OpenAPI→hey-api |
| 10 | [React Client](10-react-client.md) | The feature directories under `Client.React/src/features/`, TanStack Query/Zustand, hey-api, shared hub connections, dialog system, i18n, SPA serving |
| 11 | [Hosting & Deployment](11-hosting-and-deployment.md) | Aspire AppHost, desktop launcher, publish profiles, legacy/manual cleanup |
| 12 | [Security & Privacy](12-security-and-privacy.md) | Egress boundary, secret handling, loopback/Host-Origin, redaction, node-local AI ops, Development Mode execution boundary |
| 13 | [Testing & Validation](13-testing-and-validation.md) | Test topology, validation commands, E2E, RC evidence |
| 14 | [Image Generation](14-image-generation.md) | Local stable-diffusion.cpp: `sd-server` supervisor, serialized job coordinator, encrypted image store |
| 15 | [Knowledge Base / RAG](15-knowledge-base.md) | Offline document KB: ingestion pipeline, hybrid FTS+vector search, reranker, agent tools |
| 16 | [Code Organization Conventions](16-code-conventions.md) | Where a type/file goes: `*ServiceModels.cs`, DTO aggregation, mapper colocation, feature-folder rules, load-bearing suppressions |
| 17 | [Writing Tests](17-writing-tests.md) | Contributor authoring guide: which project a test belongs in, the `TestServerWebAppFactory` patterns, parallelism keys, migration/hub/hosted-service/React/E2E recipes, how to run a scoped subset |
| 18 | [Training](18-training.md) | Local fine-tuning: the `Providers.Training` uv/Python runtime, dataset generation, training runs, export/promote/eval |
| 19 | [Compute Tools](19-compute-tools.md) | Sandboxed code execution: the `run_python` tool, process-sandbox isolation, uv-pinned venv (numpy/scipy/sympy), security/gating (WriteExecute + approval-required, profile-opt-in), Linux-only v1, operator enablement |
| 20 | [Benchmarks](20-benchmarks.md) | Task suites and long-context probes, freeze fan-out and cell ranking, verifiable criteria incl. `pythonTests` execution scoring, pairwise Bradley-Terry and paired-difference intervals, quant fidelity (perplexity/KLD, base-logit cache, comparability digest), the four-kind work queue, scheduled matrices and the training hand-off, export schema 4 |
| 21 | [Graph Workflows](21-graph-workflows.md) | Operator-authored DAGs: the graph contract and its validation rules, the run and node-run lifecycles, the node kinds including direct LLM calls, chat inputs and decision models and their documents, Standard vs Chat graphs, the route family and the `graphWorkflowChanged` hub contract, the React editor and run view, the options table, and the one-shot Open Canvas import |
| 22 | [Workflow Engines Divergence Register](22-workflow-engines-divergence-register.md) | Dev Workflows vs. Graph Workflows: status/decision vocabulary, approval model, restart/reconciler behavior, the cross-node fix loop, persistence and hub shape, MAF/MEAI boundary, feature flags — what was deliberately dropped and what is an unintentional gap, with convergence explicitly deferred. Dev Workflows themselves are [page 25](25-dev-workflows.md); this register carries only the comparison |
| 23 | [External Apps](23-external-apps.md) | Curated containerised applications: the catalog document and its fingerprint, the engine-owned container runtime layer beside the sandbox SPI, the container policy and what it verifies on read-back, per-instance storage and the storage-wipe helper container, the lifecycle and the boot reconciler, runtime selection and the daemon identity pin, the `ExternalApps:Enabled` kill switch and what disabling does not do |
| 24 | [Audio Transcription](24-audio-transcription.md) | Local speech-to-text on whisper.cpp: the supervised `whisper-server` runtime in brief, the encrypted session and segment tables and their AAD layout, the batch upload path (streaming multipart, engine-owned temp slot, container sniffing, engine-side ffmpeg transcode, `Seq` from 1), the session route family, the React feature area, the never-persist-audio rule and its four enforcement points, the options table, and what the live capture slices have not built yet |
| 25 | [Development Workflows](25-dev-workflows.md) | Templated graphs over a code work item: the database-as-truth tick and its serialized-write invariant, the agent/tool/dev-task lanes and the write declaration, decomposition and the task-package contract, the provenance rule that gates an apply, the cross-node fix loop and its budgets, operator retry reasons and the task statuses that carry none, telemetry and rule-set policy text, artifact promotion, startup recovery, and the options table |

## Conventions in this wiki

- Every structural claim cites source as `path/to/File.cs` (and symbol names where useful).
- Every page carries a `Reviewed:` date on line 3. When you change a subsystem, update the matching page.
- Architecture invariants (egress, secrets, loopback, node-local privacy ops) are **rules**, not
  suggestions — see [Security & Privacy](12-security-and-privacy.md) and
  [Architecture Overview](01-architecture-overview.md).
