import type { IconProps } from "@tabler/icons-react";
import {
	IconBinaryTree2,
	IconCpu,
	IconDashboard,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconPlugConnected,
	IconRobot,
	IconServerCog,
	IconSettings,
} from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

// Capability flags that gate individual navigation entries (top-level or nested). A link with no
// capability is always shown; a link with a capability is shown only when that node capability is on.
type NavigationCapabilityKey = "agentManagement" | "mcpServers" | "scheduler" | "modelFit" | "loadedModels" | "preview" | "cloudSettings";

interface INavigationNestedLink {
	translationKey: string;
	to: string;
	onClick?: () => void;
	capability?: NavigationCapabilityKey;
}

export interface INavigationLink {
	id: string;
	icon: ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>;
	translationKey: string;
	to?: string;
	links?: INavigationNestedLink[];
	onClick?: () => void;
	capability?: NavigationCapabilityKey;
}

// Full link set with the related node pages collapsed into groups (Models / Settings / Automation). A group
// entry has no `to` of its own — it is a pure expand/collapse toggle whose children carry the routes. The
// exported navigationLinks below is this list with capability-gated children removed (and any group left
// empty dropped) — see the filter at the bottom of this file. The nav bars stay capability-unaware.
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
	// Models group: installed models (always) plus the model-fit recommendations page, which is gated on the
	// static modelFit capability. With modelFit off the group keeps just Installed.
	{
		id: "models",
		icon: IconCpu,
		translationKey: "navigation.models",
		links: [
			{ translationKey: "navigation.modelsInstalled", to: nodeRoutePaths.models },
			{ translationKey: "navigation.recommendations", to: nodeRoutePaths.modelRecommendations, capability: "modelFit" },
			{ translationKey: "navigation.loadedModels", to: nodeRoutePaths.loadedModels, capability: "loadedModels" },
		],
	},
	// Settings group: node settings (always) + cloud settings (gated on cloudSettings capability — hidden in
	// local-only/LocalTester builds where no CentralPlatform:BaseUrl is configured). When cloudSettings is
	// off the group collapses to just Node Settings and still renders (group never becomes empty).
	{
		id: "settings",
		icon: IconSettings,
		translationKey: "navigation.settingsGroup",
		links: [
			{ translationKey: "navigation.nodeSettings", to: nodeRoutePaths.nodeSettings },
			{ translationKey: "navigation.cloudSettings", to: nodeRoutePaths.cloudSettings, capability: "cloudSettings" },
		],
	},
	// Automation group: agents / MCP servers / scheduler are each gated on their own capability; tools is
	// always available, so the group never collapses to empty.
	{
		id: "automation",
		icon: IconRobot,
		translationKey: "navigation.automationGroup",
		links: [
			{ translationKey: "navigation.agents", to: nodeRoutePaths.agents, capability: "agentManagement" },
			{ translationKey: "navigation.skills", to: nodeRoutePaths.skills, capability: "agentManagement" },
			{ translationKey: "navigation.mcp", to: nodeRoutePaths.mcp, capability: "mcpServers" },
			{ translationKey: "navigation.scheduler", to: nodeRoutePaths.scheduler, capability: "scheduler" },
			{ translationKey: "navigation.tools", to: nodeRoutePaths.tools },
		],
	},
	// Open Canvas (Preview) workflow builder — a standalone top-level entry gated on the preview capability.
	{
		id: "preview",
		icon: IconBinaryTree2,
		translationKey: "navigation.preview",
		to: nodeRoutePaths.preview,
		capability: "preview",
	},
	// Manager group: runtime overview (always) plus the approved-images page, which is gated on the static
	// modelFit capability. With modelFit off the group keeps just Overview.
	{
		id: "manager",
		icon: IconServerCog,
		translationKey: "navigation.manager",
		links: [
			{ translationKey: "navigation.overview", to: nodeRoutePaths.manager },
			{ translationKey: "navigation.approvedImages", to: nodeRoutePaths.approvedImages, capability: "modelFit" },
		],
	},
	{
		id: "invocations",
		icon: IconListDetails,
		translationKey: "navigation.invocations",
		to: nodeRoutePaths.invocations,
	},
];

// A nav target is active when the current path equals it, or is a sub-path of it (so /models/123 still
// highlights the Models → Installed entry). The home route ("/") only matches exactly. Shared by both nav
// bars so the active-route rule stays in one place.
export function matchesNavRoute(pathname: string, to: string | undefined): boolean {
	if (!to) {
		return false;
	}

	if (to === "/") {
		return pathname === "/";
	}

	return pathname === to || pathname.startsWith(`${to}/`);
}

const isCapabilityEnabled = (capability?: NavigationCapabilityKey): boolean => (capability ? nodeCapabilities[capability] : true);

// Capability-gated navigation links: drop any top-level entry whose capability is off, filter each group's
// children by their capability, then drop a group that ends up with no children. The nav bars render this
// filtered list so they never need to reason about capabilities themselves.
export const navigationLinks: INavigationLink[] = allNavigationLinks
	.filter((link) => isCapabilityEnabled(link.capability))
	.map((link) =>
		link.links ? { ...link, links: link.links.filter((nestedLink) => isCapabilityEnabled(nestedLink.capability)) } : link,
	)
	.filter((link) => !link.links || link.links.length > 0);
