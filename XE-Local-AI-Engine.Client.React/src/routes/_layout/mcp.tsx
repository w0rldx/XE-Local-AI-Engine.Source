import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { McpServersPage } from "@/features/mcp/pages/McpServersPage";

export const Route = createFileRoute("/_layout/mcp")({
	// Capability gate (loop P4): when mcpServers is off the route is hidden — navigating to it redirects
	// home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.mcpServers) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: McpServersPage,
});
