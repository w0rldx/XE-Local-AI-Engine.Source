import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { TrainingPage } from "@/features/training/pages/TrainingPage";

export const Route = createFileRoute("/_layout/training/")({
	// Capability gate (training): while training is off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.training) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: TrainingPage,
});
