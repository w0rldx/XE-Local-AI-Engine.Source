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
}

export interface NodeCapabilityConfig {
	readonly chat: ChatCapabilities;
	readonly binding: boolean;
	readonly dashboard: boolean;
	readonly nodeSettings: boolean;
	readonly cloudSettings: boolean;
	readonly modelManagement: boolean;
	readonly runtimeManager: boolean;
	readonly invocationMonitor: boolean;
	readonly agentManagement: boolean;
	readonly mcpServers: boolean;
	readonly scheduler: boolean;
	readonly modelFit: boolean;
	readonly loadedModels: boolean;
	readonly preview: boolean;
}

export const nodeCapabilities: NodeCapabilityConfig = {
	chat: {
		localRuntime: true,
		localModelManagement: true,
		// catalog ships with RC (time/date + calculator); toggle OFF by default, user-toggleable
		localTools: true,
		toolApprovals: false,
		conversationFeedback: true,
		// server-side SQLite is the source of truth; node has no client Dexie/offline queue (offline-first is N/A for the local node)
		offlineFirst: false,
		encryptedConversations: false,
		clientNodeRouting: false,
		fileAttachments: false,
		imageAttachments: false,
		// Agent management is on by default (CRUD, playbook, templates, eval, resolver all built) — mirrors
		// nodeCapabilities.agentManagement. Repeated here so ChatCapabilityGates derives showAgentControls
		// without a cross-capability dependency.
		agentManagement: true,
	},
	binding: true,
	dashboard: true,
	nodeSettings: true,
	cloudSettings: true,
	modelManagement: true,
	runtimeManager: true,
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
};

export const nodeRoutePaths = {
	home: "/",
	chat: "/chat",
	dashboard: "/dashboard",
	binding: "/node-binding",
	nodeSettings: "/node-settings",
	cloudSettings: "/cloud-settings",
	models: "/models",
	manager: "/manager",
	invocations: "/invocations",
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
	// llmfit model recommendations page — gated on nodeCapabilities.modelFit
	modelRecommendations: "/model-recommendations",
	// llmfit approved reference images page (read-only) — gated on nodeCapabilities.modelFit
	approvedImages: "/approved-images",
	// loaded-models live overview + eject page — gated on nodeCapabilities.loadedModels
	loadedModels: "/loaded-models",
	// Open Canvas (Preview) workflow builder page — gated on nodeCapabilities.preview
	preview: "/preview",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];
