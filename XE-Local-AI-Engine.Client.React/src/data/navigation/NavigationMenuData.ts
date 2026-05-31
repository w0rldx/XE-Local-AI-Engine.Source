import type { IconProps } from "@tabler/icons-react";
import {
	IconCloudCog,
	IconCpu,
	IconDashboard,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconPlug,
	IconPlugConnected,
	IconRobot,
	IconServerCog,
	IconSettings,
	IconTools,
} from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

interface INavigationNestedLink {
	translationKey: string;
	to: string;
	onClick?: () => void;
}

export interface INavigationLink {
	id: string;
	icon: ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>;
	translationKey: string;
	to?: string;
	collapseIdentifier?: string;
	links?: INavigationNestedLink[];
	onClick?: () => void;
}

// Full link set including the capability-gated agents entry. The exported navigationLinks below is this
// list filtered by the active node capabilities — see the filter at the bottom of this file.
const allNavigationLinks: INavigationLink[] = [
	{ id: "home", icon: IconHome, translationKey: "navigation.home", to: nodeRoutePaths.home },
	{
		id: "dashboard",
		icon: IconDashboard,
		translationKey: "navigation.dashboard",
		to: nodeRoutePaths.dashboard,
	},
	{
		id: "chat",
		icon: IconMessageCircle,
		translationKey: "navigation.chat",
		to: nodeRoutePaths.chat,
	},
	{
		id: "binding",
		icon: IconPlugConnected,
		translationKey: "navigation.binding",
		to: nodeRoutePaths.binding,
	},
	{
		id: "node-settings",
		icon: IconSettings,
		translationKey: "navigation.nodeSettings",
		to: nodeRoutePaths.nodeSettings,
	},
	{
		id: "cloud-settings",
		icon: IconCloudCog,
		translationKey: "navigation.cloudSettings",
		to: nodeRoutePaths.cloudSettings,
	},
	{
		id: "models",
		icon: IconCpu,
		translationKey: "navigation.models",
		to: nodeRoutePaths.models,
	},
	{
		id: "manager",
		icon: IconServerCog,
		translationKey: "navigation.manager",
		to: nodeRoutePaths.manager,
	},
	{
		id: "invocations",
		icon: IconListDetails,
		translationKey: "navigation.invocations",
		to: nodeRoutePaths.invocations,
	},
	{
		id: "tools",
		icon: IconTools,
		translationKey: "navigation.tools",
		to: nodeRoutePaths.tools,
	},
	// Agent management link is gated on the static agentManagement capability (agent-management). Filtered out of
	// the rendered menu below when the capability is off, so the nav bars stay capability-unaware.
	{
		id: "agents",
		icon: IconRobot,
		translationKey: "navigation.agents",
		to: nodeRoutePaths.agents,
	},
	// MCP server management link is gated on the static mcpServers capability (dynamic tool-catalog). Filtered out of
	// the rendered menu below when the capability is off, mirroring the agents entry.
	{
		id: "mcp",
		icon: IconPlug,
		translationKey: "navigation.mcp",
		to: nodeRoutePaths.mcp,
	},
];

// Capability-gated navigation links: identity for everything except the agents/mcp entries, which are hidden
// when their respective node capability (agentManagement / mcpServers) is off. The nav bars render this
// filtered list so they never need to reason about capabilities themselves.
export const navigationLinks: INavigationLink[] = allNavigationLinks.filter((link) => {
	if (link.id === "agents") {
		return nodeCapabilities.agentManagement;
	}
	if (link.id === "mcp") {
		return nodeCapabilities.mcpServers;
	}
	return true;
});
