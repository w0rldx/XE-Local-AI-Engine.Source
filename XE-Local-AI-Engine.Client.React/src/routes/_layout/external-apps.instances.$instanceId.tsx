import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { ExternalAppInstanceDetailPage } from "@/features/externalApps/pages/ExternalAppInstanceDetailPage";

export const Route = createFileRoute("/_layout/external-apps/instances/$instanceId")({
	beforeLoad: () => {
		if (!nodeCapabilities.externalApps) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: ExternalAppInstanceDetailRoute,
});

// Thin router adapter: the page stays router-free (it is rendered directly in unit tests), so the instance id is read
// here and handed down as a prop. No search params — the detail tab is not worth a shareable URL.
function ExternalAppInstanceDetailRoute() {
	const { instanceId } = Route.useParams();
	return <ExternalAppInstanceDetailPage instanceId={instanceId} />;
}
