import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { AgentsPage } from "@/features/agents/pages/AgentsPage";

export const Route = createFileRoute("/_layout/agents")({
	// Capability gate (loop P3): when agentManagement is off the route is hidden — navigating to it
	// redirects home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.agentManagement) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: AgentsPage,
});
