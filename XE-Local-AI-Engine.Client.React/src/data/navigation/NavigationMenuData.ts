import type { IconProps } from "@tabler/icons-react";
import {
	IconApps,
	IconBinaryTree2,
	IconChartHistogram,
	IconCpu,
	IconDatabase,
	IconFlask,
	IconHome,
	IconListDetails,
	IconMessageCircle,
	IconMicrophone,
	IconPhoto,
	IconPlug,
	IconRobot,
	IconSchool,
	IconSettings,
	IconSitemap,
} from "@tabler/icons-react";
import type { ForwardRefExoticComponent, RefAttributes } from "react";

import type { UiMode } from "@/capabilities/NodeCapabilities";
import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { useGraphWorkflowCapability } from "@/features/graphWorkflows/queries/useGraphWorkflows";

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
	| "benchmarks"
	| "training"
	| "workSessions"
	| "devWorkflows"
	| "graphWorkflows"
	| "integrations"
	| "externalApps"
	| "transcription"
	| "invocationMonitor";

type NavigationIcon = ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>;

interface INavigationNestedLink {
	translationKey: string;
	to: string;
	onClick?: () => void;
	capability?: NavigationCapabilityKey;
	// Whether this leaf is part of the Simple navigation. Explicit on EVERY leaf, never inferred: an omitted flag is a
	// forgotten classification, which NavigationMenuData.test.ts fails on rather than letting it default silently.
	simple: boolean;
	// Set on a Simple leaf whose group would otherwise hold it alone: in Simple mode the leaf is rendered as a top-level
	// entry at its group's position (and needs its own id + icon for that), and the emptied group is dropped. Advanced
	// mode never reads this, so today's grouping and order are byte-identical there.
	promoteInSimple?: { id: string; icon: NavigationIcon };
}

export interface INavigationLink {
	id: string;
	icon: NavigationIcon;
	translationKey: string;
	to?: string;
	links?: INavigationNestedLink[];
	onClick?: () => void;
	capability?: NavigationCapabilityKey;
	// Only a top-level LEAF carries this; a group's visibility in Simple mode is decided by its surviving children, so
	// setting it on a group would be a second, contradictable authority. The test enforces both halves of that rule.
	simple?: boolean;
}

// Full link set with the related node pages collapsed into groups (Models / Settings / Automation / Preview). A group
// entry has no `to` of its own — it is a pure expand/collapse toggle whose children carry the routes. The
// exported navigationLinks below is this list with capability-gated children removed (and any group left
// empty dropped) — see the filter at the bottom of this file. The nav bars stay capability-unaware.
// Exported for the classification guard in NavigationMenuData.test.ts, which has to see EVERY leaf — including the
// ones the capability filter below removes from the default build — to prove none of them is missing its `simple` flag.
export const allNavigationLinks: INavigationLink[] = [
	{ id: "home", icon: IconHome, translationKey: "navigation.home", to: nodeRoutePaths.home, simple: true },
	{
		id: "chat",
		icon: IconMessageCircle,
		translationKey: "navigation.chat",
		to: nodeRoutePaths.chat,
		simple: true,
	},
	{
		id: "knowledgeBase",
		icon: IconDatabase,
		translationKey: "navigation.knowledgeBase",
		to: nodeRoutePaths.knowledgeBase,
		capability: "knowledgeBase",
		simple: true,
	},
	// Models group: installed models (always) plus the model-fit recommendations page, which is gated on the
	// static modelFit capability. With modelFit off the group keeps just Installed.
	{
		id: "models",
		icon: IconCpu,
		translationKey: "navigation.models",
		links: [
			{ translationKey: "navigation.modelsInstalled", to: nodeRoutePaths.models, simple: true },
			{
				translationKey: "navigation.recommendations",
				to: nodeRoutePaths.modelRecommendations,
				capability: "modelFit",
				simple: true,
			},
			{ translationKey: "navigation.loadedModels", to: nodeRoutePaths.loadedModels, capability: "loadedModels", simple: true },
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
			{ translationKey: "navigation.nodeSettings", to: nodeRoutePaths.nodeSettings, simple: true },
			{ translationKey: "navigation.cloudSettings", to: nodeRoutePaths.cloudSettings, capability: "cloudSettings", simple: true },
			{
				translationKey: "navigation.externalProviders",
				to: nodeRoutePaths.externalProviders,
				capability: "externalProviders",
				simple: true,
			},
			// Advanced-only in the nav, but NOT removed: the header's "Report Problem" button opens the same capture in
			// both modes, so a Simple operator who hits a bug still has the path (see useReportProblem).
			{ translationKey: "navigation.diagnostics", to: nodeRoutePaths.diagnostics, simple: false },
		],
	},
	// Automation group: agents / MCP servers / scheduler are each gated on their own capability; tools is
	// always available, so the group never collapses to empty.
	{
		id: "automation",
		icon: IconRobot,
		translationKey: "navigation.automationGroup",
		links: [
			{ translationKey: "navigation.commands", to: nodeRoutePaths.commands, simple: false },
			// The one Simple child of this group, so in Simple mode it is promoted to a top-level entry and the group is
			// dropped rather than rendered as a collapsible holding a single item.
			{
				translationKey: "navigation.agents",
				to: nodeRoutePaths.agents,
				capability: "agentManagement",
				simple: true,
				promoteInSimple: { id: "agents", icon: IconRobot },
			},
			{ translationKey: "navigation.workSessions", to: nodeRoutePaths.workSessions, capability: "workSessions", simple: false },
			{ translationKey: "navigation.skills", to: nodeRoutePaths.skills, capability: "agentManagement", simple: false },
			{ translationKey: "navigation.customTools", to: nodeRoutePaths.customTools, capability: "agentManagement", simple: false },
			{ translationKey: "navigation.mcp", to: nodeRoutePaths.mcp, capability: "mcpServers", simple: false },
			{ translationKey: "navigation.scheduler", to: nodeRoutePaths.scheduler, capability: "scheduler", simple: false },
			// Advanced-only, and NOT removed: it is the read-only catalog of what the four authoring surfaces above
			// register, so in Simple mode — where none of them is offered — it would list nothing and lead nowhere.
			{ translationKey: "navigation.tools", to: nodeRoutePaths.tools, simple: false },
			// Advanced-only, appended rather than placed among the authoring surfaces above: this is operator
			// forensics over what an agent already did on this computer, not another thing to author.
			{ translationKey: "navigation.agentRuns", to: nodeRoutePaths.agentRuns, simple: false },
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
			{
				translationKey: "navigation.integrationTriggers",
				to: nodeRoutePaths.integrationTriggers,
				capability: "integrations",
				simple: false,
			},
			{
				translationKey: "navigation.integrationSessions",
				to: nodeRoutePaths.integrationSessions,
				capability: "integrations",
				simple: false,
			},
			{
				translationKey: "navigation.integrationExecutions",
				to: nodeRoutePaths.integrationExecutions,
				capability: "integrations",
				simple: false,
			},
			{
				translationKey: "navigation.integrationKeys",
				to: nodeRoutePaths.integrationKeys,
				capability: "integrations",
				simple: false,
			},
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
			{
				translationKey: "navigation.externalAppsCatalog",
				to: nodeRoutePaths.externalApps,
				capability: "externalApps",
				simple: false,
			},
			{
				translationKey: "navigation.externalAppsInstalled",
				to: nodeRoutePaths.externalAppsInstalled,
				capability: "externalApps",
				simple: false,
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
			// Images and Transcription are the group's only Simple children, and a "Preview" heading is itself an
			// Advanced idea, so in Simple mode both are promoted to top-level entries and the group is dropped.
			{
				translationKey: "navigation.images",
				to: nodeRoutePaths.images,
				capability: "images",
				simple: true,
				promoteInSimple: { id: "images", icon: IconPhoto },
			},
			{ translationKey: "navigation.development", to: nodeRoutePaths.development, capability: "development", simple: false },
			// Labelled "Workflow Runs", not "Development Workflows" (C42): sitting next to "Development" the module name
			// reads as its sibling, and the two are not siblings — this one lists work items, their runs and their nodes.
			{ translationKey: "navigation.devWorkflows", to: nodeRoutePaths.devWorkflows, capability: "devWorkflows", simple: false },
			{
				translationKey: "navigation.transcription",
				to: nodeRoutePaths.transcription,
				capability: "transcription",
				simple: true,
				promoteInSimple: { id: "transcription", icon: IconMicrophone },
			},
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
		simple: false,
	},
	{
		id: "benchmarks",
		icon: IconFlask,
		translationKey: "navigation.benchmarks",
		to: nodeRoutePaths.benchmarks,
		capability: "benchmarks",
		simple: false,
	},
	// Training group. Each child carries its own capability (like Models / Automation / Preview), so the generic
	// empty-group filter drops the whole group if `training` is ever turned off again — it ships on today.
	{
		id: "training",
		icon: IconSchool,
		translationKey: "navigation.trainingGroup",
		links: [
			{
				translationKey: "navigation.trainingDatasets",
				to: nodeRoutePaths.trainingDatasets,
				capability: "training",
				simple: false,
			},
			{ translationKey: "navigation.training", to: nodeRoutePaths.training, capability: "training", simple: false },
			{
				translationKey: "navigation.trainingComparisons",
				to: nodeRoutePaths.trainingComparisons,
				capability: "training",
				simple: false,
			},
		],
	},
	{
		id: "invocations",
		icon: IconListDetails,
		translationKey: "navigation.invocations",
		to: nodeRoutePaths.invocations,
		capability: "invocationMonitor",
		simple: false,
	},
	{
		id: "usage",
		icon: IconChartHistogram,
		translationKey: "navigation.usage",
		to: nodeRoutePaths.usage,
		simple: true,
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

// Server switches: a feature the node's operator turned off at runtime (today only `GraphWorkflows:Enabled=false`).
// The compile-time filter above cannot see it, so the nav bars apply this one live, before the mode filter. Pure,
// and dropping a group that ends up empty, exactly like the capability filter.
export function filterNavigationLinksByDisabledCapabilities(
	links: readonly INavigationLink[],
	disabled: ReadonlySet<string>,
): INavigationLink[] {
	const isShown = (link: { readonly capability?: NavigationCapabilityKey }): boolean =>
		link.capability === undefined || !disabled.has(link.capability);
	return links
		.filter(isShown)
		.map((link) => (link.links ? { ...link, links: link.links.filter(isShown) } : link))
		.filter((link) => !link.links || link.links.length > 0);
}

const NO_DISABLED_CAPABILITIES: ReadonlySet<NavigationCapabilityKey> = new Set<NavigationCapabilityKey>();
const GRAPH_WORKFLOWS_DISABLED: ReadonlySet<NavigationCapabilityKey> = new Set<NavigationCapabilityKey>(["graphWorkflows"]);

// The capabilities this node's server reports switched off. Unknown (loading, failed) reads as on: hiding an entry on a
// failed read would lock the operator out of a page the node still serves.
export function useServerDisabledNavigationCapabilities(): ReadonlySet<NavigationCapabilityKey> {
	const { data } = useGraphWorkflowCapability({ enabled: nodeCapabilities.graphWorkflows });
	return data?.enabled === false ? GRAPH_WORKFLOWS_DISABLED : NO_DISABLED_CAPABILITIES;
}

// The SECOND, independent filter, layered on top of the capability one above. It is a separate pure function rather
// than another branch inside that computation because the two gates have incompatible lifetimes: `navigationLinks` is
// computed ONCE at module-eval time from compile-time constants, while `uiMode` is a server value that changes without
// a rebuild (the Node Settings toggle) and must re-filter live. So the nav bars compose them — capability first, mode
// second — inside the `useMemo` they already have.
//
// Capability still wins: this runs on the already-capability-filtered list, so a leaf compiled off stays off in either
// mode. `advanced` is the identity function, which is what pins Advanced mode to today's navigation byte for byte.
export function filterNavigationLinksByUiMode(links: readonly INavigationLink[], uiMode: UiMode): INavigationLink[] {
	if (uiMode !== "simple") {
		return [...links];
	}

	const simpleLinks: INavigationLink[] = [];

	for (const link of links) {
		if (!link.links) {
			if (link.simple === true) {
				simpleLinks.push(link);
			}

			continue;
		}

		const keptChildren = link.links.filter((nestedLink) => nestedLink.simple);

		// A promoted child leaves its group and takes the group's position, so the operator does not meet a collapsible
		// holding one item — or a "Preview"/"Automation" heading that is itself an Advanced idea.
		for (const nestedLink of keptChildren) {
			if (nestedLink.promoteInSimple === undefined) {
				continue;
			}

			simpleLinks.push({
				id: nestedLink.promoteInSimple.id,
				icon: nestedLink.promoteInSimple.icon,
				translationKey: nestedLink.translationKey,
				to: nestedLink.to,
				onClick: nestedLink.onClick,
				capability: nestedLink.capability,
				simple: true,
			});
		}

		const remainingChildren = keptChildren.filter((nestedLink) => nestedLink.promoteInSimple === undefined);
		// A group whose children all went away is dropped whole, never rendered as an empty expandable — the same rule
		// the capability filter above applies, for the same reason.
		if (remainingChildren.length > 0) {
			simpleLinks.push({ ...link, links: remainingChildren });
		}
	}

	return simpleLinks;
}

// Only what the rail actually renders may claim a sub-path: an entry whose capability is off has no link to be
// the "more specific" one, and would otherwise silently take the highlight away from its visible parent.
const navigationTargets: readonly string[] = navigationLinks
	.flatMap((link) => [link.to, ...(link.links ?? []).map((nestedLink) => nestedLink.to)])
	.filter((to): to is string => to !== undefined);
