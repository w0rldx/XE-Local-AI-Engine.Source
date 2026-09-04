import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { IntegrationTriggersPage } from "@/features/integrations/pages/IntegrationTriggersPage";

export const Route = createFileRoute("/_layout/integrations/triggers")({
	// Capability gate (external integrations): when integrations is off the route is hidden — navigating to it
	// redirects home, matching the nav group being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.integrations) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: IntegrationTriggersPage,
});
