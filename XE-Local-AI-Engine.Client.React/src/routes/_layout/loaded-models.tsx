import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";

export const Route = createFileRoute("/_layout/loaded-models")({
	// Capability gate (loaded-models): when loadedModels is off the route is hidden — navigating to it redirects
	// home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.loadedModels) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: LoadedModelsPage,
});
