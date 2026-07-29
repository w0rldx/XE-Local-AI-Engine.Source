# AI runtime developer notes

Last reviewed: 2026-06-24

> For the full, current runtime architecture (host llama.cpp supervisor, provider seams, agent mode),
> see the [Developer Wiki](wiki/Home.md) — especially
> [Local Runtime & Providers](wiki/03-local-runtime-and-providers.md) and
> [Agent Mode](wiki/04-agent-mode.md). This page keeps only the narrow AI-seam maintenance rules.

This page explains the local AI/ML integration seams that future maintainers should understand before changing model providers, agent behavior, tool execution, or embeddings.

## Semantic documentation anchors

For comment cleanup and AI-agent retrieval, use stable runtime terms instead of historical implementation labels. The backend-wide source map is in [Backend commentary map](backend-commentary-map.md). AI-runtime comments should prefer these anchors:

- agent definition resolution;
- orchestration topology, handoffs, checkpoints, and tool approval;
- playbook actions, feedback insights, golden conversations, eval gates, relevance retrieval, and cohort monitoring;
- MCP tool registry and offered-tool resolution;
- local model/provider seams, embeddings, and provider-neutral chat clients.


## Runtime boundaries

- `XE-Local-AI-Engine.AI.Agent` owns Microsoft Agent Framework wiring: agent construction, orchestration runs, tool registries, and the shared `IChatClient` pipeline.
- `XE-Local-AI-Engine.Client.Application` owns application decisions: persisted credentials, local model selection, runtime-package projection, AgentHome tools, MCP discovery, playbook actions, and chat persistence.
- `XE-Local-AI-Engine.Providers.*` owns provider adapters. Provider-specific SDK types should stay inside provider projects; application code should depend on `ILocalModelProvider`, `IChatClient`, or `IEmbeddingGenerator`.
- `XE-Local-AI-Engine.Providers.LlamaServer` owns the host model-runtime lifecycle (supervising the `llama-server` child process, GPU variant selection, and binary acquisition). The former `XE-Local-AI-Engine.HostAgent.*` connection layer and the Docker/container sandbox were removed in the 2026-06-17 runtime re-architecture, and **the model-runtime path carries no container dependency** — inference is a supervised host child process with a driver-only footprint. ([ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md) later permitted Docker for **Development Mode execution only**; that is a separate feature behind the `ISandboxRuntimeProvider` seam and does not reach any runtime boundary on this page.) Browser/API DTOs must not expose provider secrets, worker credentials, HMAC secrets, or host-only paths.

## External library expectations

The AI stack changes quickly. Re-check upstream docs before changing these seams:

| Area | Current repository usage | Upstream reference |
| --- | --- | --- |
| Microsoft.Extensions.AI | Provider-neutral `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` abstractions. | <https://learn.microsoft.com/dotnet/ai/ai-extensions> |
| Microsoft Agent Framework | `AIAgent`, `ChatClientAgent`, handoff workflows, tool invocation, and workflow event streaming. | <https://learn.microsoft.com/agent-framework/overview/> |
| llama.cpp (`llama-server`) | Primary local runtime: host child process supervised in-process; GGUF chat + embeddings via an OpenAI-compatible endpoint (`XE-Local-AI-Engine.Providers.LlamaServer`). | <https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md> |
| HuggingFace | GGUF model discovery + download (`XE-Local-AI-Engine.Providers.HuggingFace`). | <https://huggingface.co/docs/hub/gguf> |
| Ollama | Optional/legacy local provider (present but de-orchestrated from Aspire dev): inventory, pull/delete/warm/unload, chat, embeddings via OllamaSharp. | <https://docs.ollama.com/api> and <https://docs.ollama.com/capabilities/embeddings> |
| Codex OAuth (cloud) | ChatGPT-subscription OAuth chat provider used as the cloud option (`XE-Local-AI-Engine.Providers.CodexOAuth`). | <https://platform.openai.com/docs/api-reference/responses> |

## Maintenance rules

1. Keep the base `IChatClient` registration in the host composition root, then decorate it through `AddLocalAiAgentRuntime`.
2. Resolve offered tools by name through the built-in, ClientLocal, and MCP registries before an agent run starts. Unknown offered names must be dropped, not executed.
3. Keep executable tool schemas derived from the executable or registered descriptor; do not hand-copy model-visible JSON schemas into unrelated DTOs.
4. Keep Agent Framework workflow types behind the `IOrchestrationRunSession` / factory boundary so `.Client.Application` stays framework-type-agnostic.
5. Treat Ollama model names, context-length metadata, and embedding dimensions as provider observations, not hard-coded invariants.
6. Treat cloud-provider credentials (Codex OAuth tokens) and the HuggingFace token as local secrets. They may configure a chat client or download path but must not be logged, returned to the browser, or included in transcripts.
7. Preserve cancellation-token flow through chat, embedding, tool, MCP, and sandbox operations.

## Validation after AI changes

Run the normal backend/frontend validation for production code changes. For AI-specific changes, also add or update narrow tests around:

- model/provider selection and fallback;
- tool schema/approval resolution;
- orchestration event normalization;
- llama.cpp process supervision, GGUF model resolution, and Ollama model inventory/context parsing;
- cloud-provider (Codex OAuth) credential validation and redaction;
- embedding service behavior for empty input and cancellation.
