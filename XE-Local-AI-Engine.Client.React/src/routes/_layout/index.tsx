import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";

export const Route = createFileRoute("/_layout/")({
	// Chat is the landing page. `/` stays a route because every "go home" target (capability gates, setup pages, the
	// error fallback, the post-login default) points here; it carries no page of its own.
	beforeLoad: () => {
		throw redirect({ to: nodeRoutePaths.chat });
	},
});
