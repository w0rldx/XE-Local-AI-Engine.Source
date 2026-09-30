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

// Reasons whose server message is worth showing verbatim: the sandbox reasons carry a fixed, path-free remedy, a missing
// command carries the jail-PATH hint, and the exit and startup reasons carry the exit code and the server's scrubbed
// stderr tail, the only clue to why it died.
export function hasEngineDetail(reason: McpConnectionFailureReason | null): boolean {
	return (
		reason === "ServerNotFound" ||
		reason === "SandboxUnavailable" ||
		reason === "SandboxRefused" ||
		reason === "ServerExited" ||
		reason === "ServerStartupFailed"
	);
}
