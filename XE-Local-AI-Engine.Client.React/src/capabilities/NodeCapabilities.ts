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
	readonly modelManagement: boolean;
	readonly invocationMonitor: boolean;
	readonly agentManagement: boolean;
	readonly mcpServers: boolean;
	readonly scheduler: boolean;
	readonly modelFit: boolean;
	readonly loadedModels: boolean;
	readonly preview: boolean;
	readonly knowledgeBase: boolean;
	// Local image-generation surface (stable-diffusion.cpp text-to-image). Enabled by default: the runtime has been
	// live-GPU verified end-to-end on target hardware, so the nav entry + /images route ship on by default as a
	// flagship feature.
	readonly images: boolean;
	// Dedicated durable software-development workflow. The surface ships in every build; the authenticated
	// runtime capability controls whether its actions are available on this node.
	readonly development: boolean;
}

export const nodeCapabilities: NodeCapabilityConfig = {
	chat: {
		localRuntime: true,
		localModelManagement: true,
		// catalog ships with RC (time/date + calculator); toggle OFF by default, user-toggleable
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
		// stay off (no OCR/vision path in v1).
		fileAttachments: true,
		imageAttachments: false,
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
	modelManagement: true,
	invocationMonitor: true,
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
	// Image generation (stable-diffusion.cpp) surface. ON by default — the runtime module (Lanes A–D) is built and
	// has been live-GPU verified end-to-end on target hardware; ships as a flagship feature.
	images: true,
	// The route ships by default. DevelopmentPage resolves the authenticated server capability before exposing
	// projects or actions, so an operator kill switch still fails closed without requiring a separate frontend build.
	development: true,
};

export const nodeRoutePaths = {
	home: "/",
	chat: "/chat",
	dashboard: "/dashboard",
	binding: "/node-binding",
	nodeSettings: "/node-settings",
	cloudSettings: "/cloud-settings",
	models: "/models",
	invocations: "/invocations",
	// agent token-usage dashboard (per-provider/model/day rollups) — operator observability, always available like
	// invocations (both are backed by operator-gated endpoints; the authenticated _layout is the operator gate).
	usage: "/usage",
	// local tools catalog page — extension seam: MCP tools will populate the same list later
	tools: "/tools",
	// agent definition management page (agent-management) — gated on nodeCapabilities.agentManagement
	agents: "/agents",
	// node skill library page (agent-skills) — gated on nodeCapabilities.agentManagement (an agent-mode feature)
	skills: "/skills",
	// MCP server management page (dynamic tool-catalog) — gated on nodeCapabilities.mcpServers
	mcp: "/mcp",
	// Quartz scheduler management page — gated on nodeCapabilities.scheduler
	scheduler: "/scheduler",
	// local model advisor page (recommendations + hardware profile + GGUF browse/download) — gated on nodeCapabilities.modelFit
	modelRecommendations: "/model-recommendations",
	// loaded-models live overview + eject page — gated on nodeCapabilities.loadedModels
	loadedModels: "/loaded-models",
	// Open Canvas (Preview) workflow builder page — gated on nodeCapabilities.preview
	preview: "/preview",
	// Knowledge-base management page (document ingestion + semantic search) — gated on nodeCapabilities.knowledgeBase
	knowledgeBase: "/knowledge-base",
	// Local image-generation page (text-to-image) — gated on nodeCapabilities.images
	images: "/images",
	// Dedicated Development Mode project/task workflow — gated on nodeCapabilities.development.
	development: "/development",
	// Local-only diagnostics panel (frontend error snapshots) — always available.
	diagnostics: "/diagnostics",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];
