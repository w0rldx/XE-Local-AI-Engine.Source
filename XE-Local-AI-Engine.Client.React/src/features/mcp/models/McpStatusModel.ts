import type { McpConnectionFailureReason, McpConnectionStatus } from "@/features/mcp/models/McpServerToolsModels";

export function statusColor(status: McpConnectionStatus): string {
	if (status === "connected") {
		return "teal";
	}
	if (status === "error") {
		return "red";
	}
	if (status === "connecting") {
		// Amber for the transient in-progress state (enabled, refresh in flight) — distinct from the red
		// "error" and gray "disabled".
		return "yellow";
	}
	// "disabled" and any unknown status fall back to a neutral gray badge.
	return "gray";
}

// The sandbox reasons are the only ones whose server message is an engine-authored remedy worth showing verbatim.
export function hasEngineDetail(reason: McpConnectionFailureReason | null): boolean {
	return reason === "SandboxUnavailable" || reason === "SandboxRefused";
}
