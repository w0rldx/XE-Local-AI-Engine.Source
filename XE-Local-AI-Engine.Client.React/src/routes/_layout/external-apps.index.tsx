import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export const Route = createFileRoute("/_layout/external-apps/")({
	// The group has no landing page of its own — /external-apps always resolves to the catalog, or home when the
	// capability is compiled off (matching the nav group being filtered out of NavigationMenuData). A catalog link on
	// the bare prefix would stay highlighted on Installed and on every detail route, since matchesNavRoute treats a
	// prefix as a match. beforeLoad throws on every path, so no component is reachable and none is declared.
	beforeLoad: () => {
		throw redirect({ to: nodeCapabilities.externalApps ? nodeRoutePaths.externalApps : nodeRoutePaths.home });
	},
});
