import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ApprovedImagesPage } from "@/features/model-fit/pages/ApprovedImagesPage";

export const Route = createFileRoute("/_layout/approved-images")({
	// Capability gate (model-fit): when modelFit is off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.modelFit) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ApprovedImagesPage,
});
