import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ExternalAppsInstalledPage } from "@/features/externalApps/pages/ExternalAppsInstalledPage";

export const Route = createFileRoute("/_layout/external-apps/installed")({
	beforeLoad: () => {
		if (!nodeCapabilities.externalApps) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ExternalAppsInstalledPage,
});
