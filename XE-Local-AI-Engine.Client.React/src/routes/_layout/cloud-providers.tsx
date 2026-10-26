import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { CloudProviders } from "@/features/cloud-providers/pages/CloudProviders";

export const Route = createFileRoute("/_layout/cloud-providers")({
	// Capability gate: with both provider capabilities off the route is unreachable, matching the nav link being
	// filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.cloudSettings && !nodeCapabilities.externalProviders) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: CloudProviders,
});
