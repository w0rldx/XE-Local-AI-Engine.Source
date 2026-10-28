import type { XeLocalAiEngineClientEndpointsWorkspacesV1WorkspaceResponse } from "@/core/api/generated";

export interface McpWorkspace {
	readonly id: string;
	readonly alias: string;
	readonly mode: "read-only";
}

export function toMcpWorkspace(response: XeLocalAiEngineClientEndpointsWorkspacesV1WorkspaceResponse): McpWorkspace {
	return {
		id: response.workspaceId,
		alias: response.alias,
		mode: "read-only",
	};
}
