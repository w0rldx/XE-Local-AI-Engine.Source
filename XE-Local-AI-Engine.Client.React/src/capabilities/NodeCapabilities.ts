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
}

export const nodeCapabilities: NodeCapabilityConfig = {
	chat: {
		localRuntime: true,
		localModelManagement: true,
		// catalog ships with RC (time/date + calculator); toggle OFF by default, user-toggleable — see Plans/2026-05-27-local-tools-rc-team-plan.md
		localTools: true,
		toolApprovals: false,
		conversationFeedback: true,
		// server-side SQLite is the source of truth; node has no client Dexie/offline queue — see Plans/chat-capability-gap-rc.md E (N/A-LOCAL)
		offlineFirst: false,
		encryptedConversations: false,
		clientNodeRouting: false,
		fileAttachments: false,
		imageAttachments: false,
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
	// MCP server management page (dynamic tool-catalog) — gated on nodeCapabilities.mcpServers
	mcp: "/mcp",
} as const;

export type NodeRouteId = keyof typeof nodeRoutePaths;
export type NodeRoutePath = (typeof nodeRoutePaths)[NodeRouteId];
