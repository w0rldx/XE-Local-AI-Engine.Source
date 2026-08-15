import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { DatasetsPage } from "@/features/training/pages/DatasetsPage";

export const Route = createFileRoute("/_layout/training/datasets")({
	// Capability gate (training): while training is off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData. Each training route carries its own gate
	// because they are sibling routes, not children of a guarded parent.
	beforeLoad: () => {
		if (!nodeCapabilities.training) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: DatasetsPage,
});
