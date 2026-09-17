import type { IconProps } from "@tabler/icons-react";
import {
	IconApps,
	IconBinaryTree2,
	IconChartHistogram,
	IconCpu,
	IconDashboard,
	IconDatabase,
	IconFlask,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconPlug,
	IconPlugConnected,
	IconRobot,
	IconSchool,
	IconSettings,
	IconSitemap,
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
	| "knowledgeBase"
	| "images"
	| "development"
	| "cloudSettings"
	| "externalProviders"
	| "dashboard"
	| "binding"
	| "benchmarks"
	| "training"
	| "workSessions"
	| "devWorkflows"
	| "graphWorkflows"
	| "integrations"
	| "externalApps"
	| "transcription";

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
	// Settings group: node settings (always) + cloud settings + external providers (each gated on its own capability,
	// both on by default — Cloud Settings hosts the local cloud-provider credentials (Codex OAuth + Azure Foundry) and
	// External Providers the operator's own OpenAI-compatible endpoints; neither needs a Central Platform pairing).
	// The group always renders (Node Settings + Diagnostics are ungated).
	{
		id: "settings",
		icon: IconSettings,
		translationKey: "navigation.settingsGroup",
		links: [
			{ translationKey: "navigation.nodeSettings", to: nodeRoutePaths.nodeSettings },
			{ translationKey: "navigation.cloudSettings", to: nodeRoutePaths.cloudSettings, capability: "cloudSettings" },
			{ translationKey: "navigation.externalProviders", to: nodeRoutePaths.externalProviders, capability: "externalProviders" },
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
			{ translationKey: "navigation.workSessions", to: nodeRoutePaths.workSessions, capability: "workSessions" },
			{ translationKey: "navigation.skills", to: nodeRoutePaths.skills, capability: "agentManagement" },
			{ translationKey: "navigation.customTools", to: nodeRoutePaths.customTools, capability: "agentManagement" },
			{ translationKey: "navigation.mcp", to: nodeRoutePaths.mcp, capability: "mcpServers" },
			{ translationKey: "navigation.scheduler", to: nodeRoutePaths.scheduler, capability: "scheduler" },
			{ translationKey: "navigation.tools", to: nodeRoutePaths.tools },
		],
	},
	// External Integrations group: every child carries the same `integrations` capability, so the generic
	// empty-group filter below drops the whole group when the capability is compiled off. The group entry itself has
	// no `to` — /integrations is a real URL prefix, but its index route only redirects (see routes/_layout/
	// integrations.index.tsx), so the children carry the routes.
	{
		id: "integrations",
		icon: IconPlug,
		translationKey: "navigation.integrationsGroup",
		links: [
			{ translationKey: "navigation.integrationTriggers", to: nodeRoutePaths.integrationTriggers, capability: "integrations" },
			{ translationKey: "navigation.integrationSessions", to: nodeRoutePaths.integrationSessions, capability: "integrations" },
			{
				translationKey: "navigation.integrationExecutions",
				to: nodeRoutePaths.integrationExecutions,
				capability: "integrations",
			},
			{ translationKey: "navigation.integrationKeys", to: nodeRoutePaths.integrationKeys, capability: "integrations" },
		],
	},
	// External Apps group, directly after Integrations: both children carry the same `externalApps` capability, so
	// the generic empty-group filter below drops the whole group when the capability is compiled off. It ships ON;
	// the node's own `ExternalApps:Enabled` is a separate switch, and turning THAT off leaves this group visible
	// (the capability is compile-time) while every route answers 404. Like Integrations the group entry has no `to`
	// — /external-apps only redirects to the catalog — and the instance detail route has no nav entry, like the
	// work-session and workflow detail pages.
	{
		id: "externalApps",
		icon: IconApps,
		translationKey: "navigation.externalAppsGroup",
		links: [
			{ translationKey: "navigation.externalAppsCatalog", to: nodeRoutePaths.externalApps, capability: "externalApps" },
			{
				translationKey: "navigation.externalAppsInstalled",
				to: nodeRoutePaths.externalAppsInstalled,
				capability: "externalApps",
			},
		],
	},
	// Preview group: collects experimental / preview features under one menu point. Image Generation
	// (stable-diffusion.cpp), Development Mode (the registered-source worktree workflow), Workflow Runs and
	// Transcription (whisper.cpp) live here — none of them is confidently verified end-to-end yet, so each is
	// presented as a preview surface rather than a flagship top-level entry.
	// Each child carries its OWN capability (the group itself is ungated, like Models / Automation), so turning one
	// capability off drops only that child and the generic empty-group filter below removes the group once every
	// child is off. That keeps every child's nav visibility exactly aligned with its route's own capability redirect.
	{
		id: "preview",
		icon: IconBinaryTree2,
		translationKey: "navigation.previewGroup",
		links: [
			{ translationKey: "navigation.images", to: nodeRoutePaths.images, capability: "images" },
			{ translationKey: "navigation.development", to: nodeRoutePaths.development, capability: "development" },
			// Labelled "Workflow Runs", not "Development Workflows" (C42): sitting next to "Development" the module name
			// reads as its sibling, and the two are not siblings — this one lists work items, their runs and their nodes.
			{ translationKey: "navigation.devWorkflows", to: nodeRoutePaths.devWorkflows, capability: "devWorkflows" },
			{ translationKey: "navigation.transcription", to: nodeRoutePaths.transcription, capability: "transcription" },
		],
	},
	// Graph Workflows is a TOP-LEVEL entry, not a Preview child: it is the successor to the retired Open Canvas and
	// carries its own capability, which ships ON since S4 — an operator build that turns it off drops this entry.
	{
		id: "graphWorkflows",
		icon: IconSitemap,
		translationKey: "navigation.graphWorkflows",
		to: nodeRoutePaths.graphWorkflows,
		capability: "graphWorkflows",
	},
	{
		id: "benchmarks",
		icon: IconFlask,
		translationKey: "navigation.benchmarks",
		to: nodeRoutePaths.benchmarks,
		capability: "benchmarks",
	},
	// Training group. Each child carries its own capability (like Models / Automation / Preview), so the generic
	// empty-group filter drops the whole group if `training` is ever turned off again — it ships on today.
	{
		id: "training",
		icon: IconSchool,
		translationKey: "navigation.trainingGroup",
		links: [
			{ translationKey: "navigation.trainingDatasets", to: nodeRoutePaths.trainingDatasets, capability: "training" },
			{ translationKey: "navigation.training", to: nodeRoutePaths.training, capability: "training" },
			{ translationKey: "navigation.trainingComparisons", to: nodeRoutePaths.trainingComparisons, capability: "training" },
		],
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

// Every navigation <Link> passes this. TanStack's Link stamps `aria-current="page"` on itself whenever ITS OWN match
// is active, after the caller's props, so it cannot be overridden — and its default match is a prefix match that knows
// nothing about sibling entries (/training lit up on /training/datasets next to the real current page). Exact matching
// confines Link's stamp to the one case where it agrees with matchesNavRoute, which stays the single rule.
export const navLinkActiveOptions = { exact: true } as const;

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

	if (pathname === to) {
		return true;
	}

	// A sub-path highlights its parent entry only while no other entry claims it more specifically: /training is a
	// prefix of /training/datasets, and without this both siblings were marked as the current page at once.
	return (
		pathname.startsWith(`${to}/`) &&
		!navigationTargets.some((other) => other.length > to.length && matchesPrefix(pathname, other))
	);
}

const matchesPrefix = (pathname: string, to: string): boolean => pathname === to || pathname.startsWith(`${to}/`);

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

// Only what the rail actually renders may claim a sub-path: an entry whose capability is off has no link to be
// the "more specific" one, and would otherwise silently take the highlight away from its visible parent.
const navigationTargets: readonly string[] = navigationLinks
	.flatMap((link) => [link.to, ...(link.links ?? []).map((nestedLink) => nestedLink.to)])
	.filter((to): to is string => to !== undefined);
