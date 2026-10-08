import { useQuery } from "@tanstack/react-query";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { listMcpServersOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

/**
 * MCP server display names by tool slug, read from the same generated list query (and cache key) the MCP page uses;
 * chat reads it through core instead of importing the mcp feature. Gated like that page (`mcpServers`) and on the
 * caller's `enabled`. A server without a slug yet (it has never connected) is simply absent.
 */
export function useMcpServerNames({ enabled }: { enabled: boolean }) {
	return useQuery({
		...withResponseValidation(listMcpServersOptions()),
		enabled: enabled && nodeCapabilities.mcpServers,
		select: (data) => new Map((data.items ?? []).flatMap((server) => (server.slug ? [[server.slug, server.name] as const] : []))),
	});
}
