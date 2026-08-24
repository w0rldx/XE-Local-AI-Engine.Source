import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { WorkSessionDetailPage } from "@/features/workSessions/pages/WorkSessionDetailPage";

export const Route = createFileRoute("/_layout/work-sessions/$sessionId")({
	beforeLoad: () => {
		if (!nodeCapabilities.workSessions) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: WorkSessionDetailRoute,
});

// Thin router adapter: WorkSessionDetailPage stays router-free for its unit tests, matching the benchmarks route.
function WorkSessionDetailRoute() {
	const { sessionId } = Route.useParams();
	return <WorkSessionDetailPage sessionId={sessionId} />;
}
