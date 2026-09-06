export interface ChatCapabilities {
	readonly localRuntime: boolean;
	readonly localModelManagement: boolean;
	readonly localTools: boolean;
	readonly toolApprovals: boolean;
	readonly conversationFeedback: boolean;
	readonly offlineFirst: boolean;
	readonly encryptedConversations: boolean;
	readonly clientNodeRouting: boolean;
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
	readonly binding: boolean;
	readonly dashboard: boolean;
	readonly nodeSettings: boolean;
	// When false the Cloud Settings nav entry is hidden and the /cloud-settings route is inaccessible.
	// Cloud Settings is a LOCAL cloud-provider surface (Codex OAuth + Azure Foundry credentials, stored
	// encrypted on this node) — it does NOT require a Central Platform pairing, so it is on by default.
	readonly cloudSettings: boolean;
	// When false the External Providers nav entry is hidden and the /external-providers route is inaccessible.
	// Like Cloud Settings this is a LOCAL surface — operator-declared OpenAI-compatible endpoints and their models,
	// stored encrypted on this node — so it needs no Central Platform pairing and is on by default.
	readonly externalProviders: boolean;
	readonly modelManagement: boolean;
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
	readonly preview: boolean;
	readonly knowledgeBase: boolean;
	// Local image-generation surface (stable-diffusion.cpp text-to-image). Enabled by default, but surfaced as a
	// PREVIEW feature: its nav entry is a child of the Preview group (next to Open Canvas) rather than a top-level
	// link, because the runtime is not yet confidently verified end-to-end. This flag alone gates both the nav child
	// and the /images route — it is independent of the `preview` flag, which gates only Open Canvas.
	readonly images: boolean;
	// Durable software-development workflow (Development Mode). The surface ships in every build; the authenticated
	// runtime capability controls whether its actions are available on this node. It is an EXPERIMENTAL surface, so
	// its nav entry is a child of the Preview group (next to Open Canvas and Image Generation) rather than a
	// top-level link. This flag alone gates both the nav child and the /development route.
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
	// Join, Pause, End) with a canvas editor and a live run view. Ships GATED OFF — S4 flips it once the surface is
	// verified end to end. It is an experimental surface, so its nav entry is a child of the Preview group rather than
	// a top-level link. The node ALSO has its own `GraphWorkflows:Enabled` switch, which 404s the API — this flag only
	// decides whether the surface is offered.
	readonly graphWorkflows: boolean;
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
		encryptedConversations: false,
		clientNodeRouting: false,
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
	// Central-Platform surfaces (Node Binding + Dashboard) only make sense once the node is paired to a Central
	// Platform. In the local-only (LocalTester) profile — no CentralPlatform:BaseUrl configured — they are hidden
	// so the menu does not show dead "disconnected / not paired" pages. Flip both to true when paired.
	binding: false,
	dashboard: false,
	nodeSettings: true,
	// Cloud Settings is a LOCAL cloud-provider surface (Codex OAuth sign-in + Azure Foundry connection/models,
	// stored encrypted on this node). It needs no Central Platform pairing, so it is on by default.
	cloudSettings: true,
	// External Providers hosts the node-local OpenAI-compatible connections (base URL, optional key, declared
	// Local/Cloud trust, and the operator-registered models). Node-local encrypted storage, no pairing — on by default.
	externalProviders: true,
	modelManagement: true,
	invocationMonitor: true,
	benchmarks: true,
	// Training group (datasets, runs, comparisons): live-verified end to end on this box 2026-08-15.
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
	// Open Canvas (Preview) workflow builder surface. On by default; node-local SQLite-backed workflow CRUD with
	// live per-node run output streamed over the preview SignalR hub. Workflows persist; run output is transient
	// (never logged/indexed) and the run-output store is empty on every page load.
	preview: true,
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
	// Off until S4: the editor and run view ship in the build but are not offered yet.
	graphWorkflows: false,
};

export const nodeRoutePaths = {
	home: "/",
	chat: "/chat",
	dashboard: "/dashboard",
	binding: "/node-binding",
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
	skills: "/skills",
	customTools: "/custom-tools",
	mcp: "/mcp",
	scheduler: "/scheduler",
	modelRecommendations: "/model-recommendations",
	loadedModels: "/loaded-models",
	preview: "/preview",
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
	// Graph Workflows editor + run view — gated on nodeCapabilities.graphWorkflows (off by default). One route; the
	// definition, run, node and tab selections live in its search params.
	graphWorkflows: "/graph-workflows",
	// Local-only diagnostics panel (frontend error snapshots) — always available.
	diagnostics: "/diagnostics",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];
