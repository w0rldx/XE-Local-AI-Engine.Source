import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { Dashboard } from "@/features/dashboard/pages/Dashboard";

export const Route = createFileRoute("/_layout/dashboard")({
	// Capability gate (Central-Platform surface): when dashboard is off (local-only builds with no Central Platform)
	// the route is hidden — navigating to it redirects home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.dashboard) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: Dashboard,
});
