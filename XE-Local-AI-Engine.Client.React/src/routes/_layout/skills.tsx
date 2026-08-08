import { createFileRoute, redirect } from "@tanstack/react-router";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { SkillsPage } from "@/features/skills/pages/SkillsPage";

export const Route = createFileRoute("/_layout/skills")({
	// Capability gate (agent-skills): skills are an agent-mode feature, so the route is gated on agentManagement —
	// when it is off the route is hidden and navigating to it redirects home, matching the nav link being filtered
	// out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.agentManagement) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	component: SkillsPage,
});
