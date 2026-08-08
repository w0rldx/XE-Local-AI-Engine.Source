import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { SchedulerPage } from "@/features/scheduler/pages/SchedulerPage";

export const Route = createFileRoute("/_layout/scheduler")({
	// Capability gate (Quartz scheduler): when scheduler is off the route is hidden — navigating to it redirects
	// home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.scheduler) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: SchedulerPage,
});
