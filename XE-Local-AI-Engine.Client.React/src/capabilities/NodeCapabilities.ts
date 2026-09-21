export interface ChatCapabilities {
	readonly localRuntime: boolean;
	readonly localModelManagement: boolean;
	readonly localTools: boolean;
	readonly toolApprovals: boolean;
	readonly conversationFeedback: boolean;
	readonly offlineFirst: boolean;
	readonly fileAttachments: boolean;
	readonly imageAttachments: boolean;
	// When true the chat composer shows the agent-mode toggle + agent picker. Derives from the node's
	// agentManagement surface capability — same flag, surfaced here so ChatCapabilityGates can derive
	// showAgentControls without reaching outside the chat capabilities bag.
	readonly agentManagement?: boolean;
	// Node-level availability of the client voice (TTS) feature surface. The ACTUAL gate for voice UI also
	// requires the operator-owned manifest.Enabled (server-state) — this flag only marks the surface as present
	// in the build. buildChatUiCapabilities combines it with manifest.Enabled to derive showVoiceControls.
	readonly voice?: boolean;
	// When true the chat composer shows the "Use Knowledge Base" toggle (opt-in plain-chat grounding). Mirrors the
	// node-level knowledgeBase surface capability — same flag, surfaced here so ChatCapabilityGates can derive
	// showKnowledgeBaseControls without reaching outside the chat capabilities bag.
	readonly knowledgeBase?: boolean;
}

export interface NodeCapabilityConfig {
	readonly chat: ChatCapabilities;
	readonly nodeSettings: boolean;
	// When false the Cloud Settings nav entry is hidden and the /cloud-settings route is inaccessible.
	// Cloud Settings is a LOCAL cloud-provider surface (Codex OAuth + Azure Foundry credentials, stored
	// encrypted on this node), so it is on by default.
	readonly cloudSettings: boolean;
	// When false the External Providers nav entry is hidden and the /external-providers route is inaccessible.
	// Like Cloud Settings this is a LOCAL surface — operator-declared OpenAI-compatible endpoints and their models,
	// stored encrypted on this node — so it is on by default.
	readonly externalProviders: boolean;
	readonly modelManagement: boolean;
	// Invocation monitor: the node's record of agent and tool invocations. This flag alone gates both the nav entry
	// and the /invocations route. Compile-time only — there is no matching server switch, so the monitor endpoint
	// stays reachable whichever way this is set.
	readonly invocationMonitor: boolean;
	readonly benchmarks: boolean;
	// Training group (dataset generation, training runs, comparisons). Endpoints ship registered and Operator-gated
	// regardless of this flag; it only shows or hides the nav group and its routes. Compile-time flag only — it is not
	// a server-side kill switch.
	readonly training: boolean;
	readonly agentManagement: boolean;
	readonly mcpServers: boolean;
	readonly scheduler: boolean;
	readonly modelFit: boolean;
	readonly loadedModels: boolean;
	readonly knowledgeBase: boolean;
	// Local image-generation surface (stable-diffusion.cpp text-to-image). Enabled by default, but surfaced as a
	// PREVIEW feature: its nav entry is a child of the Preview group rather than a top-level link, because the
	// runtime is not yet confidently verified end-to-end. This flag alone gates both the nav child and the
	// /images route.
	readonly images: boolean;
	// Durable software-development workflow (Development Mode). The surface ships in every build; the authenticated
	// runtime capability controls whether its actions are available on this node. It is an EXPERIMENTAL surface, so
	// its nav entry is a child of the Preview group (next to Image Generation) rather than a top-level link.
	// This flag alone gates both the nav child and the /development route.
	readonly development: boolean;
	// Long-running agent Work Sessions (own plan, findings, artifacts and checkpoints, driven by a detached
	// supervisor). Gates both the nav entry and the two /work-sessions routes. The node ALSO has its own
	// `WorkSessions:Enabled` switch, which 404s the API — this flag only decides whether the surface is offered.
	readonly workSessions: boolean;
	// Development Workflows: durable, graph-based work items whose runs dispatch agent / tool / gate node-runs and
	// survive an engine restart. Gates the nav child and the two /development-workflows routes. The node ALSO has its
	// own `DevWorkflows:Enabled` switch, which 404s the API — this flag only decides whether the surface is offered.
	// It is an EXPERIMENTAL surface, so its nav entry is a child of the Preview group rather than a top-level link.
	readonly devWorkflows: boolean;
	// External Integrations: operator-managed triggers, API keys, sessions and executions for the loopback
	// integration-api. Gates the nav group and all four /integrations/* routes. Build-time flag only — the node's
	// own Operator policy on the admin endpoints is the real authorization boundary, and the external
	// integration-api family is authenticated by its own xeint_ key scheme regardless of this flag.
	readonly integrations: boolean;
	// Graph Workflows: operator-authored DAGs of the eight v1 node kinds (Start, Agent, Tool, Condition, Parallel,
	// Join, Pause, End) with a canvas editor and a live run view. Ships ON since S4 verified the editor, the run
	// engine and the run view end to end. Its nav entry is a top-level link (promoted out of the Preview group when
	// Open Canvas was removed). The node ALSO has its own `GraphWorkflows:Enabled` switch, which 404s the API — this
	// flag only decides whether the surface is offered.
	readonly graphWorkflows: boolean;
	// External Apps: a curated catalog of applications XE installs and supervises on this computer, their installed
	// instances and one detail page per instance. Gates the nav group and all four /external-apps routes. The node
	// ALSO has its own `ExternalApps:Enabled` switch, which 404s the API and the hub negotiate — this flag only
	// decides whether the surface is OFFERED, and it is compile-time: the backend switch can neither reveal these
	// routes nor hide them. On since S5 flipped it ahead of its live browser round.
	readonly externalApps: boolean;
	// Local audio transcription (whisper.cpp): upload a recording, get a timestamped transcript. Gates the nav child
	// and the two /transcription routes. Shipped under the PREVIEW nav group next to Image Generation — the runtime is
	// a child process that is not yet verified end-to-end, and live capture only arrives in a later slice. The node
	// ALSO has its own `Transcription:Enabled` switch, which 404s the API — this flag only decides whether the surface
	// is offered, and it is compile-time.
	readonly transcription: boolean;
}

export const nodeCapabilities: NodeCapabilityConfig = {
	chat: {
		localRuntime: true,
		localModelManagement: true,
		// The local-tool catalog and composer controls are available by default for built-in and discovered MCP tools.
		localTools: true,
		// Local tool-approval responder: the chat stream surfaces a pending MCP-tool approval and the waiting
		// tool card renders Approve/Deny controls wired to the loopback resolve endpoint. Enabled by default now that
		// the responder ships — MCP tools default to approval-on, so an agent-mode turn with a connected MCP server
		// exercises it live.
		toolApprovals: true,
		conversationFeedback: true,
		// server-side SQLite is the source of truth; node has no client Dexie/offline queue (offline-first is N/A for the local node)
		offlineFirst: false,
		// File attachments are live: a user can attach documents (txt/md/csv/json/code/pdf/docx) to a
		// conversation; extracted text grounds plain chat and stages into AgentHome for agent mode. Images
		// route to vision-capable models via the local mmproj projector path (gated per-model on
		// isMultimodalCapable — see ChatInputArea's activeModelMultimodal).
		fileAttachments: true,
		imageAttachments: true,
		// Agent management is on by default (CRUD, playbook, templates, eval, resolver all built) — mirrors
		// nodeCapabilities.agentManagement. Repeated here so ChatCapabilityGates derives showAgentControls
		// without a cross-capability dependency.
		agentManagement: true,
		// Client voice (TTS) surface is present in the build. It stays dev-gated and additionally requires the
		// operator-owned manifest.Enabled before any voice UI shows (see buildChatUiCapabilities).
		voice: true,
		// Knowledge-base grounding surface for plain chat is present in the build (mirrors nodeCapabilities.knowledgeBase).
		// Drives the composer "Use Knowledge Base" toggle via buildChatUiCapabilities.showKnowledgeBaseControls.
		knowledgeBase: true,
	},
	nodeSettings: true,
	// Cloud Settings is a LOCAL cloud-provider surface (Codex OAuth sign-in + Azure Foundry connection/models,
	// stored encrypted on this node), so it is on by default.
	cloudSettings: true,
	// External Providers hosts the node-local OpenAI-compatible connections (base URL, optional key, declared
	// Local/Cloud trust, and the operator-registered models). Node-local encrypted storage — on by default.
	externalProviders: true,
	modelManagement: true,
	invocationMonitor: true,
	benchmarks: true,
	// Training group (datasets, runs, comparisons): live-verified end to end on a dev host 2026-08-15.
	training: true,
	// Agent definition authoring surface (agent-management). On by default; node-local SQLite-backed CRUD.
	agentManagement: true,
	// MCP server registration surface (dynamic tool-catalog). On by default; node-local SQLite-backed CRUD. Registered
	// servers are disabled until explicitly enabled, and every discovered MCP tool defaults to approval-on.
	mcpServers: true,
	// Quartz scheduler management surface. On by default; node-local SQLite-backed CRUD. Jobs are disabled until
	// explicitly enabled, and job parameters are stored encrypted (never returned on the wire).
	scheduler: true,
	// llmfit model-fit surface (Model recommendations + read-only Approved reference images). On by default; reads
	// are cache-only (never run llmfit), refreshes delegate to the scheduler. Approved image references are
	// code/seed-owned and never editable from the browser.
	modelFit: true,
	// Loaded-models live overview + eject surface. On by default; polls the runtime's in-memory model set (RAM/VRAM)
	// and offers a graceful eject (unload from memory after any in-flight generation finishes — never disk delete).
	loadedModels: true,
	// Knowledge-base management surface (document ingestion + semantic search). On by default; node-local
	// SQLite-backed document store with a background extract→chunk→embed→index pipeline, live status over the
	// knowledge-base SignalR hub. Selective encryption: source document blobs and display names are encrypted
	// at rest; the extracted chunk text and its FTS search index are stored unencrypted in local SQLite.
	knowledgeBase: true,
	// Image generation (stable-diffusion.cpp) surface. ON by default — the runtime module (Lanes A–D) is built — but
	// it ships under the Preview nav group, not as a top-level entry, until it is confidently verified end-to-end.
	images: true,
	// The route ships by default, under the Preview nav group (Development Mode is an experimental surface).
	// DevelopmentPage resolves the authenticated server capability before exposing projects or actions, so an
	// operator kill switch still fails closed without requiring a separate frontend build.
	development: true,
	workSessions: true,
	devWorkflows: true,
	integrations: true,
	// On since S4: the editor and the run view are verified end to end, so the surface is offered by default.
	graphWorkflows: true,
	// On since S5: the capability is compile-time, so the live browser round cannot reach the catalog, the installed
	// list or a detail page until it is true. Flipped BEFORE the round; the backend `ExternalApps:Enabled` default
	// follows it, after the round has passed on this tree.
	externalApps: true,
	// On by default so the Preview group offers it; the backend `Transcription:Enabled` switch remains the operational
	// kill switch.
	transcription: true,
};

export const nodeRoutePaths = {
	home: "/",
	chat: "/chat",
	nodeSettings: "/node-settings",
	cloudSettings: "/cloud-settings",
	externalProviders: "/external-providers",
	models: "/models",
	invocations: "/invocations",
	benchmarks: "/benchmarks",
	training: "/training",
	trainingDatasets: "/training/datasets",
	trainingComparisons: "/training/comparisons",
	// agent token-usage dashboard (per-provider/model/day rollups) — operator observability, always available like
	// invocations (both are backed by operator-gated endpoints; the authenticated _layout is the operator gate).
	usage: "/usage",
	// Local tools catalog page for built-in and discovered MCP tools.
	tools: "/tools",
	// Node-wide user-authored slash commands. This belongs to Automation but is not an agent capability: only the
	// human composer resolves commands, and every authenticated node exposes the management surface.
	commands: "/commands",
	agents: "/agents",
	// Read-only AgentHome run history (operator forensics), ungated for the same reason as usage/invocations: it is
	// backed by an operator-gated endpoint and the authenticated _layout is the gate.
	agentRuns: "/agent-runs",
	skills: "/skills",
	customTools: "/custom-tools",
	mcp: "/mcp",
	scheduler: "/scheduler",
	modelRecommendations: "/model-recommendations",
	loadedModels: "/loaded-models",
	knowledgeBase: "/knowledge-base",
	images: "/images",
	// Dedicated Development Mode project/task workflow — gated on nodeCapabilities.development.
	development: "/development",
	// Agent work sessions list — gated on nodeCapabilities.workSessions. The detail route is /work-sessions/{id}.
	workSessions: "/work-sessions",
	// Development Workflows work-item list — gated on nodeCapabilities.devWorkflows. Detail is
	// /development-workflows/{workItemId}; the run, node and tab selections live in its search params.
	devWorkflows: "/development-workflows",
	// External Integrations — four sibling routes, each with its own beforeLoad capability gate. The bare
	// /integrations prefix has no entry: its index route redirects to integrationTriggers.
	integrationTriggers: "/integrations/triggers",
	integrationSessions: "/integrations/sessions",
	integrationExecutions: "/integrations/executions",
	integrationKeys: "/integrations/keys",
	// Graph Workflows editor + run view — gated on nodeCapabilities.graphWorkflows (on by default). One route; the
	// definition, run, node and tab selections live in its search params.
	graphWorkflows: "/graph-workflows",
	// External Apps — gated on nodeCapabilities.externalApps. The bare /external-apps prefix has no
	// entry: its index route redirects to the catalog, so a Catalog nav link on /external-apps would stay highlighted
	// on Installed and on every detail route. The detail route is /external-apps/instances/{instanceId}.
	externalApps: "/external-apps/catalog",
	externalAppsInstalled: "/external-apps/installed",
	// Audio transcription — gated on nodeCapabilities.transcription. The detail route carries the router's own
	// `$sessionId` placeholder because it is a navigation target with a param, not a link destination.
	transcription: "/transcription",
	transcriptionSession: "/transcription/$sessionId",
	// Local-only diagnostics panel (frontend error snapshots) — always available.
	diagnostics: "/diagnostics",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];

// The two navigation modes the operator picks between, stored server-side as `uiMode` on the node settings. `simple`
// shows the everyday surfaces only; `advanced` shows everything this build offers, and is what an absent server value
// reads as, so a node that never answered looks exactly as it did before the mode existed.
//
// It lives beside the capability flags because it is the SECOND navigation gate, and it is a very different one: a
// capability is compile-time and decides what EXISTS, while this is a runtime preference that only decides what is
// RENDERED IN THE NAV. Every route stays reachable by its own address in either mode, and no server gate reads it.
export type UiMode = "simple" | "advanced";
