import type { IconProps } from "@tabler/icons-react";
import {
	IconBinaryTree2,
	IconChartHistogram,
	IconCpu,
	IconDashboard,
	IconDatabase,
	IconFlask,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconPlugConnected,
	IconRobot,
	IconSettings,
} from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

// Capability flags that gate individual navigation entries (top-level or nested). A link with no
// capability is always shown; a link with a capability is shown only when that node capability is on.
type NavigationCapabilityKey =
	| "agentManagement"
	| "mcpServers"
	| "scheduler"
	| "modelFit"
	| "loadedModels"
	| "preview"
	| "knowledgeBase"
	| "images"
	| "development"
	| "cloudSettings"
	| "dashboard"
	| "binding";

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

// Full link set with the related node pages collapsed into groups (Models / Settings / Automation / Preview). A group
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
		// Central-Platform surface — hidden in local-only builds (see nodeCapabilities.dashboard).
		capability: "dashboard",
	},
	{
		id: "chat",
		icon: IconMessageCircle,
		translationKey: "navigation.chat",
		to: nodeRoutePaths.chat,
	},
	{
		id: "knowledgeBase",
		icon: IconDatabase,
		translationKey: "navigation.knowledgeBase",
		to: nodeRoutePaths.knowledgeBase,
		capability: "knowledgeBase",
	},
	{
		id: "binding",
		icon: IconPlugConnected,
		translationKey: "navigation.binding",
		to: nodeRoutePaths.binding,
		// Central-Platform surface — hidden in local-only builds (see nodeCapabilities.binding).
		capability: "binding",
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
	// Settings group: node settings (always) + cloud settings (gated on cloudSettings capability, which is on by
	// default because Cloud Settings hosts the local cloud-provider credentials — Codex OAuth + Azure Foundry —
	// and needs no Central Platform pairing). The group always renders (Node Settings + Diagnostics are ungated).
	{
		id: "settings",
		icon: IconSettings,
		translationKey: "navigation.settingsGroup",
		links: [
			{ translationKey: "navigation.nodeSettings", to: nodeRoutePaths.nodeSettings },
			{ translationKey: "navigation.cloudSettings", to: nodeRoutePaths.cloudSettings, capability: "cloudSettings" },
			{ translationKey: "navigation.diagnostics", to: nodeRoutePaths.diagnostics },
		],
	},
	// Automation group: agents / MCP servers / scheduler are each gated on their own capability; tools is
	// always available, so the group never collapses to empty.
	{
		id: "automation",
		icon: IconRobot,
		translationKey: "navigation.automationGroup",
		links: [
			{ translationKey: "navigation.commands", to: nodeRoutePaths.commands },
			{ translationKey: "navigation.agents", to: nodeRoutePaths.agents, capability: "agentManagement" },
			{ translationKey: "navigation.skills", to: nodeRoutePaths.skills, capability: "agentManagement" },
			{ translationKey: "navigation.customTools", to: nodeRoutePaths.customTools, capability: "agentManagement" },
			{ translationKey: "navigation.mcp", to: nodeRoutePaths.mcp, capability: "mcpServers" },
			{ translationKey: "navigation.scheduler", to: nodeRoutePaths.scheduler, capability: "scheduler" },
			{ translationKey: "navigation.tools", to: nodeRoutePaths.tools },
		],
	},
	// Preview group: collects experimental / preview features under one menu point. Open Canvas (the MAF workflow
	// builder), Image Generation (stable-diffusion.cpp) and Development Mode (the registered-source worktree
	// workflow) all live here — none of the three is confidently verified end-to-end yet, so each is presented as a
	// preview surface rather than a flagship top-level entry.
	// Each child carries its OWN capability (the group itself is ungated, like Models / Automation), so turning one
	// capability off drops only that child and the generic empty-group filter below removes the group once every
	// child is off. That keeps every child's nav visibility exactly aligned with its route's own capability redirect.
	{
		id: "preview",
		icon: IconBinaryTree2,
		translationKey: "navigation.previewGroup",
		links: [
			{ translationKey: "navigation.preview", to: nodeRoutePaths.preview, capability: "preview" },
			{ translationKey: "navigation.images", to: nodeRoutePaths.images, capability: "images" },
			{ translationKey: "navigation.development", to: nodeRoutePaths.development, capability: "development" },
		],
	},
	{
		id: "benchmarks",
		icon: IconFlask,
		translationKey: "navigation.benchmarks",
		to: nodeRoutePaths.benchmarks,
	},
	{
		id: "invocations",
		icon: IconListDetails,
		translationKey: "navigation.invocations",
		to: nodeRoutePaths.invocations,
	},
	{
		id: "usage",
		icon: IconChartHistogram,
		translationKey: "navigation.usage",
		to: nodeRoutePaths.usage,
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
