import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { Invocations } from "@/features/invocations/pages/Invocations";

export const Route = createFileRoute("/_layout/invocations")({
	// Capability gate (invocation monitor): when invocationMonitor is off the route is hidden — navigating to it
	// redirects home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.invocationMonitor) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: Invocations,
});
