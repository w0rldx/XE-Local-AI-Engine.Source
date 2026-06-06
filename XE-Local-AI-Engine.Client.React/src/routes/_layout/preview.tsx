import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { PreviewPage } from "@/features/preview/pages/PreviewPage";

export const Route = createFileRoute("/_layout/preview")({
	// Capability gate (Open Canvas preview): when preview is off the route is hidden — navigating to it redirects
	// home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.preview) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: PreviewPage,
});
