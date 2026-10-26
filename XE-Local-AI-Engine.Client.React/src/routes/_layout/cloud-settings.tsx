import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export const Route = createFileRoute("/_layout/cloud-settings")({
	// The former Azure Foundry and Codex page, kept so old links land on the page that now holds both.
	beforeLoad: () => {
		throw redirect({ to: nodeRoutePaths.cloudProviders });
	},
});
