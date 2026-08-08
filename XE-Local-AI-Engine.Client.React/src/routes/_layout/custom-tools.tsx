import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { CustomToolsPage } from "@/features/customTools/pages/CustomToolsPage";

export const Route = createFileRoute("/_layout/custom-tools")({
	// Capability gate (agent-mode): custom tools are offered to agents, so the route is gated on agentManagement —
	// when it is off the route is hidden and navigating to it redirects home, matching the nav link being filtered out.
	beforeLoad: () => {
		if (!nodeCapabilities.agentManagement) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: CustomToolsPage,
});
