import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ImagesPage } from "@/features/images/pages/ImagesPage";

export const Route = createFileRoute("/_layout/images")({
	// Capability gate (image generation): when images is off the route is hidden — navigating to it redirects home,
	// matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.images) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ImagesPage,
});
