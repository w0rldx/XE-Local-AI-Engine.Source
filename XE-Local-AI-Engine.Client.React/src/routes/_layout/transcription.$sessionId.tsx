import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { TranscriptionSessionPage } from "@/features/transcription/pages/TranscriptionSessionPage";

export const Route = createFileRoute("/_layout/transcription/$sessionId")({
	beforeLoad: () => {
		if (!nodeCapabilities.transcription) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: TranscriptionSessionRoute,
});

// Thin router adapter: TranscriptionSessionPage stays router-free for its unit tests, matching the work-sessions route.
function TranscriptionSessionRoute() {
	const { sessionId } = Route.useParams();
	return <TranscriptionSessionPage sessionId={sessionId} />;
}
