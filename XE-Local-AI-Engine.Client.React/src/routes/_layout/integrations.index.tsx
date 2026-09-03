import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export const Route = createFileRoute("/_layout/integrations/")({
	// The group has no landing page of its own — /integrations always resolves to the triggers page, or home when
	// the capability is compiled off (matching the nav group being filtered out of NavigationMenuData). beforeLoad
	// throws on every path, so no component is reachable and none is declared.
	beforeLoad: () => {
		throw redirect({ to: nodeCapabilities.integrations ? nodeRoutePaths.integrationTriggers : nodeRoutePaths.home });
	},
});
