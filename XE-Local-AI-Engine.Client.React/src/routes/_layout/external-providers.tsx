import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ExternalProviders } from "@/features/external-providers/pages/ExternalProviders";

export const Route = createFileRoute("/_layout/external-providers")({
	// Capability gate: with externalProviders off the route is unreachable, matching the nav link being filtered out
	// of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.externalProviders) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ExternalProviders,
});
