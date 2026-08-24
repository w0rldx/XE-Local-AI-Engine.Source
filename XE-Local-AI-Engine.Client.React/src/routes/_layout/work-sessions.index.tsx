import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { WorkSessionsPage } from "@/features/workSessions/pages/WorkSessionsPage";

export const Route = createFileRoute("/_layout/work-sessions/")({
	// Capability gate: while work sessions are off the route is hidden — navigating to it redirects home, matching
	// the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.workSessions) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: WorkSessionsPage,
});
