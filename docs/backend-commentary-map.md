# Backend commentary map

Last reviewed: 2026-06-02

Use this map when updating backend comments, XML docs, or AI-agent retrieval notes. It replaces historical implementation-increment labels with stable source terms that match the current runtime.

## Cleanup rule

Source comments should describe current invariants, ownership, security boundaries, and external-library seams. Do not reintroduce historical delivery labels such as Playbook P*, Loop P*, Marker *, lane numbers, or plan-section references into `.cs` comments. If an old label still captures useful rationale, rewrite it using one of the stable terms below and link to this map or `docs/ai-runtime.md` from Markdown, not from every source hunk.

## Stable backend anchors

| Stable term | Read when changing | Primary source areas | Comment guidance |
| --- | --- | --- | --- |
| Agent definition resolution | Single-agent prompts, model selection, tool-capability filtering, or conversation-bound agent definitions. | `XE-Local-AI-Engine.Client.Application/Services/Agents/**`, `.../Services/Chat/**`, `XE-Local-AI-Engine.Client/Endpoints/Agents/**` | Explain how definitions become runtime prompts and offered tools; avoid implementation-wave labels. |
| Orchestration topology | Multi-agent/orchestrator definitions, participant routing, handoff behavior, or framework-type boundaries. | `XE-Local-AI-Engine.AI.Agent/**`, `XE-Local-AI-Engine.Client.Application/Services/Agents/**` | Use Microsoft Agent Framework terms: agent, workflow, handoff, checkpoint, and tool approval. |
| Playbook actions | Manual actions, suggested actions, analysis staging, approval, prompt injection, and enabled-action limits. | `.../Services/Agents/**`, `.../Endpoints/Agents/**`, persistence `PlaybookAction*` stores/entities | Comments should distinguish staging from enabled actions and human approval from automated analysis. |
| Feedback insights | Aggregate reads over `message_feedback` and operator-facing feedback summaries. | `XE-Local-AI-Engine.Client.Persistence/Implementation/FeedbackInsightsStore.cs`, `.../Services/Insights/**` | Preserve privacy and aggregation boundaries; do not imply per-message attribution when the signal is aggregate. |
| Golden conversations and eval gate | Golden-case CRUD, eval re-runs, deterministic assertions, judge model scoring, and promotion gating. | `.../Services/Eval/**`, `XE-Local-AI-Engine.AI.Agent/Eval/**`, `.../Endpoints/Agents/*Eval*` | Explain that eval is offline/batch evidence for promotion, not part of chat streaming. |
| Relevance retrieval | Top-k playbook injection, lexical or embedding rankers, and retrieval thresholds. | `.../Services/Agents/*Retrieval*`, chat stream/regeneration services, monitor DTOs | Comments should name retrieval thresholds and ranker behavior without historical increment labels. |
| Cohort monitoring | Before/after feedback windows, enabled-action monitoring, and advisory regression status. | `.../Services/Monitoring/**`, `IPlaybookMonitorStore`, monitor endpoints/DTOs | Emphasize that monitoring is coarse, agent-level, and advisory. |
| MCP tool registry | MCP server registration, connection lifecycle, live tool snapshots, and offered tool resolution. | `.../Services/Mcp/**`, invocation/tool registry services, local tool offer providers | Keep secret ownership and enabled-server filtering explicit. |
| Scheduler persistence and runtime | Quartz configuration, job definitions, run history, run events, cancellation, and startup reconciliation. | `.../Services/Scheduler/**`, `XE-Local-AI-Engine.Client.Persistence/**ScheduledJob**`, scheduler endpoints/hub/tests | Use Quartz terms: job, trigger, scheduler, hosted service, misfire, interruption, and run event. |
| AgentHome workspace | Workspace copy, sensitive-file exclusions, manifest recovery, run logging, memory proposals, patch preview/apply. | `.../Services/AgentHome/**`, `.../Services/Workspace/**`, related tests | Preserve why security guards exist; replace section-number references with named invariants. |
| Sandbox runtime | Local container sandbox options, gRPC sandbox control, Docker labels/network posture, and host-side sandbox service. | `.../Services/Sandbox/**`, `XE-Local-AI-Engine.HostAgent.*`, `XE-Local-AI-Engine.HostAgent.Grpc.Contracts` | Keep the host/container trust boundary and provider-neutral DTO boundary clear. |
| HostAgent lifecycle | Container lifecycle, runtime metadata, model runtime operations, and tray/host communication. | `XE-Local-AI-Engine.HostAgent.Linux/**`, `XE-Local-AI-Engine.HostAgent.Windows/**`, `XE-Local-AI-Engine.Tray/**` | Explain ownership, cleanup, and secret redaction without narrating obvious Docker calls. |
| AI provider seam | Local/remote model providers, Ollama, Azure OpenAI, embeddings, and provider-neutral abstractions. | `XE-Local-AI-Engine.Providers.*`, `.../Services/Chat/**`, `docs/ai-runtime.md` | Keep SDK-specific types inside provider projects and document provider observations as observations, not invariants. |

## External-library comment checkpoints

Before changing comments that describe library behavior, check the current upstream docs for the relevant seam and record the URL in the cleanup report:

- C# XML documentation comments and recommended tags: public XML docs may become API/Swagger text, so keep descriptions intentional.
- FastEndpoints Swagger support: endpoint summaries can override XML comments; query/body descriptions are public API documentation when surfaced.
- Microsoft.Extensions.AI: `IChatClient` and `IEmbeddingGenerator` are provider-neutral abstractions with DI, middleware, telemetry, and tool-calling extensions.
- Microsoft Agent Framework: use agent/workflow/handoff/checkpoint/tool-approval vocabulary for orchestration comments.
- Quartz.NET: use job/trigger/scheduler/hosted-service/misfire/OpenTelemetry vocabulary for scheduler comments.
- EF Core SQLite: persistence comments should distinguish provider behavior from application invariants.
- SignalR, gRPC, OpenTelemetry, Docker, and Ollama: verify official docs when comments explain transport, telemetry, container, or model-runtime behavior.

## Verification checklist for commentary edits

1. Run the PRD stale-term search over `.cs` files and document any allowlisted false positives.
2. Run the typo-sentinel search over `.cs` and Markdown files; the cleanup report intentionally spells the sentinel command in split form so the report does not self-match.
3. Inspect `git diff -- '*.cs'` and confirm every changed source hunk is comment-only/XML-doc-only.
4. Run `git diff --check`.
5. Run backend restore/build/test from `.opencode/context/project-intelligence/validation-matrix.md`, or record the exact environment blocker and next-best evidence.
