import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { DevelopmentPage } from "@/features/development/pages/DevelopmentPage";

export const Route = createFileRoute("/_layout/development")({
	beforeLoad: () => {
		if (!nodeCapabilities.development) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: DevelopmentPage,
});
