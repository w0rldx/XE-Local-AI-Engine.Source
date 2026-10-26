import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export const Route = createFileRoute("/_layout/external-providers")({
	// The former OpenAI-compatible connections page, kept so old links land on the page that now holds them.
	beforeLoad: () => {
		throw redirect({ to: nodeRoutePaths.cloudProviders });
	},
});
