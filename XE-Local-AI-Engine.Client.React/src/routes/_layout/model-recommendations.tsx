import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ModelRecommendationsPage } from "@/features/model-fit/pages/ModelRecommendationsPage";

export const Route = createFileRoute("/_layout/model-recommendations")({
	// Capability gate (model-fit): when modelFit is off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.modelFit) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ModelRecommendationsPage,
});
