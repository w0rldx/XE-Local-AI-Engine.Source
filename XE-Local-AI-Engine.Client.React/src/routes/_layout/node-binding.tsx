import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { NodeBinding } from "@/features/binding/pages/NodeBinding";

export const Route = createFileRoute("/_layout/node-binding")({
	// Capability gate (Central-Platform surface): when binding is off (local-only builds with no Central Platform)
	// the route is hidden — navigating to it redirects home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.binding) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: NodeBinding,
});
