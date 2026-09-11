import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ExternalAppCatalogPage } from "@/features/externalApps/pages/ExternalAppCatalogPage";

export const Route = createFileRoute("/_layout/external-apps/catalog")({
	// Capability gate: while External Apps are off the route is hidden — navigating to it redirects home, matching the
	// nav group being filtered out of NavigationMenuData. The node's own ExternalApps:Enabled switch is separate and
	// 404s the API; this flag decides whether the surface exists in the build at all.
	beforeLoad: () => {
		if (!nodeCapabilities.externalApps) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ExternalAppCatalogPage,
});
