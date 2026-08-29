import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { DevWorkflowsPage } from "@/features/devWorkflows/pages/DevWorkflowsPage";

export const Route = createFileRoute("/_layout/development-workflows/")({
	// Capability gate: while development workflows are off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.devWorkflows) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: DevWorkflowsPage,
});
