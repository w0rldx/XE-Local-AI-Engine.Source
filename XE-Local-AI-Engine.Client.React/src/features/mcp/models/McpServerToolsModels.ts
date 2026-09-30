// View-model for the per-server discovered-tools + connection-status panel (dynamic tool-catalog). The
// connection manager surfaces, per registered server, whether it is currently connected and which tools it
// exposed on the last refresh. A disabled server contributes no tools and reports a "disabled" status; an
// enabled server still mid-connect reports "connecting"; a server that failed to connect reports "error" with
// a redacted message.

// Connection state for a registered MCP server as seen by the node connection manager.
export type McpConnectionStatus = "connected" | "disabled" | "error" | "connecting";

// Why the last connect attempt failed, or why a connected server was lost; set only with the "error" status. The two
// sandbox reasons carry an engine-authored remedy in `error`; the exit and startup reasons append the server's scrubbed
// stderr tail to it; every other reason is a fixed server wording the panel replaces with its own.
export type McpConnectionFailureReason =
	| "Unknown"
	| "ServerNotFound"
	| "SandboxUnavailable"
	| "SandboxRefused"
	| "Timeout"
	| "Transport"
	| "Protocol"
	| "Authentication"
	| "AuthenticationRequired"
	| "Forbidden"
	| "Tls"
	| "ServerExited"
	| "SessionLost"
	| "ServerStartupFailed";

// A tool discovered from a connected MCP server. The name is the qualified executable name
// (mcp__{server}__{tool}); requiresApproval is the catalog default (ON for MCP tools).
export interface McpDiscoveredTool {
	readonly name: string;
	readonly description: string;
	readonly requiresApproval: boolean;
}

// Live status for one registered server: its connection state, any redacted error, and the tools it exposed.
export interface McpServerToolsView {
	readonly status: McpConnectionStatus;
	readonly error: string | null;
	readonly failureReason: McpConnectionFailureReason | null;
	readonly tools: readonly McpDiscoveredTool[];
}
