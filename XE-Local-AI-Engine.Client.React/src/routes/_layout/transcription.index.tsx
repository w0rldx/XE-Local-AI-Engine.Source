import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { TranscriptionPage } from "@/features/transcription/pages/TranscriptionPage";

export const Route = createFileRoute("/_layout/transcription/")({
	// Capability gate: while transcription is off the route is hidden — navigating to it redirects home, matching
	// the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.transcription) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: TranscriptionPage,
});
