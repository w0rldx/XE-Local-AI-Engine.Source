import { afterEach, describe, expect, it, vi } from "vitest";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import {
	allNavigationLinks,
	filterNavigationLinksByUiMode,
	matchesNavRoute,
	navigationLinks,
} from "@/data/navigation/NavigationMenuData";

const mockCapabilities = (overrides: Record<string, boolean>) => {
	vi.resetModules();
	vi.doMock("@/capabilities/NodeCapabilities", async () => {
		const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>("@/capabilities/NodeCapabilities");
		return {
			...actual,
			nodeCapabilities: { ...actual.nodeCapabilities, ...overrides },
		};
	});

	return import("@/data/navigation/NavigationMenuData");
};

describe("navigationLinks", () => {
	afterEach(() => {
		vi.resetModules();
		vi.doUnmock("@/capabilities/NodeCapabilities");
	});

	it("groups the related node pages under Models / Settings / Automation with flat entries around them", () => {
		expect(navigationLinks.map((link) => link.id)).toEqual([
			"home",
			"chat",
			"knowledgeBase",
			"models",
			"settings",
			"automation",
			"integrations",
			"externalApps",
			"preview",
			"graphWorkflows",
			"benchmarks",
			"training",
			"invocations",
			"usage",
		]);
		// Image generation and Development Mode are no longer top-level entries — both moved under the Preview group
		// (see the group test below).
		expect(navigationLinks.some((link) => link.id === "images")).toBe(false);
		expect(navigationLinks.some((link) => link.id === "development")).toBe(false);
	});

	it("ships the Training group on by default and hides it whole when the capability is compiled off", async () => {
		// The group was dark-shipped until the feature was live-verified (2026-08-15); it is on by default now, and the
		// compile-time capability still removes the whole group rather than leaving an empty one.
		const { navigationLinks: hiddenLinks } = await mockCapabilities({ training: false });
		expect(hiddenLinks.some((link) => link.id === "training")).toBe(false);

		const training = navigationLinks.find((link) => link.id === "training");

		expect(training?.to).toBeUndefined();
		expect(training?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.trainingDatasets,
			nodeRoutePaths.training,
			nodeRoutePaths.trainingComparisons,
		]);
	});

	it("carries Development Mode as a Preview child and hides it when its capability is off", async () => {
		const preview = navigationLinks.find((link) => link.id === "preview");
		expect(preview?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.development)).toBe(true);

		const { navigationLinks: gatedLinks } = await mockCapabilities({ development: false });
		const gatedPreview = gatedLinks.find((link) => link.id === "preview");

		expect(gatedPreview?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.development)).toBe(false);
		// It must not reappear as a top-level entry either.
		expect(gatedLinks.some((link) => link.id === "development")).toBe(false);
	});

	it("ships the External Integrations group on by default and hides it whole when the capability is compiled off", async () => {
		const integrations = navigationLinks.find((link) => link.id === "integrations");

		expect(integrations?.to).toBeUndefined();
		expect(integrations?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.integrationTriggers,
			nodeRoutePaths.integrationSessions,
			nodeRoutePaths.integrationExecutions,
			nodeRoutePaths.integrationKeys,
		]);

		// Every child carries the same capability, so the generic empty-group filter removes the whole group.
		const { navigationLinks: gatedLinks } = await mockCapabilities({ integrations: false });
		expect(gatedLinks.some((link) => link.id === "integrations")).toBe(false);
	});

	it("ships the External Apps group on by default and hides it whole when the capability is compiled off", async () => {
		const externalApps = navigationLinks.find((link) => link.id === "externalApps");

		expect(externalApps?.to).toBeUndefined();
		expect(externalApps?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.externalApps,
			nodeRoutePaths.externalAppsInstalled,
		]);

		// Every child carries the same capability, so the generic empty-group filter removes the whole group.
		const { navigationLinks: gatedLinks } = await mockCapabilities({ externalApps: false });
		expect(gatedLinks.some((link) => link.id === "externalApps")).toBe(false);
	});

	it("makes each group a pure toggle (no own route) with its children carrying the routes", () => {
		const models = navigationLinks.find((link) => link.id === "models");
		const settings = navigationLinks.find((link) => link.id === "settings");
		const automation = navigationLinks.find((link) => link.id === "automation");

		expect(models?.to).toBeUndefined();
		expect(settings?.to).toBeUndefined();
		expect(automation?.to).toBeUndefined();

		expect(models?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.models,
			nodeRoutePaths.modelRecommendations,
			nodeRoutePaths.loadedModels,
		]);
		// cloudSettings and externalProviders are both on by default (each is a node-local provider-credential
		// surface needing no Central Platform), so the settings group shows all four children.
		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.nodeSettings,
			nodeRoutePaths.cloudSettings,
			nodeRoutePaths.externalProviders,
			nodeRoutePaths.diagnostics,
		]);
		expect(automation?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.commands,
			nodeRoutePaths.agents,
			nodeRoutePaths.workSessions,
			nodeRoutePaths.skills,
			nodeRoutePaths.customTools,
			nodeRoutePaths.mcp,
			nodeRoutePaths.scheduler,
			nodeRoutePaths.tools,
			nodeRoutePaths.agentRuns,
		]);
	});

	it("keeps only Installed under Models when modelFit and loadedModels are off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ modelFit: false, loadedModels: false });
		const models = gatedLinks.find((link) => link.id === "models");

		expect(models?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.models]);
	});

	it("drops the loaded-models child from Models when loadedModels is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ loadedModels: false });
		const models = gatedLinks.find((link) => link.id === "models");

		expect(models?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.loadedModels)).toBe(false);
		// Installed is ungated, so the group never collapses to empty.
		expect(models?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.models)).toBe(true);
	});

	it("drops the agents, skills and custom-tools children from Automation when agentManagement is off", async () => {
		// Agents, Skills and Custom tools are all gated on agentManagement (agent-mode features), so they drop together.
		const { navigationLinks: gatedLinks } = await mockCapabilities({ agentManagement: false });
		const automation = gatedLinks.find((link) => link.id === "automation");

		expect(automation?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.commands,
			// Work sessions carry their OWN capability, so turning agentManagement off does not drop them.
			nodeRoutePaths.workSessions,
			nodeRoutePaths.mcp,
			nodeRoutePaths.scheduler,
			nodeRoutePaths.tools,
			// Ungated, like Commands and Tools: the run history is operator forensics, not an agent-mode capability.
			nodeRoutePaths.agentRuns,
		]);
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.skills)).toBe(false);
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.customTools)).toBe(false);
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.commands)).toBe(true);
	});

	it("drops the mcp child from Automation when mcpServers is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ mcpServers: false });
		const automation = gatedLinks.find((link) => link.id === "automation");

		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.mcp)).toBe(false);
	});

	it("shows Cloud Settings in the settings group when cloudSettings capability is on", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ cloudSettings: true });
		const settings = gatedLinks.find((link) => link.id === "settings");

		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.nodeSettings,
			nodeRoutePaths.cloudSettings,
			nodeRoutePaths.externalProviders,
			nodeRoutePaths.diagnostics,
		]);
	});

	it("hides Cloud Settings and keeps Node Settings when cloudSettings capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ cloudSettings: false });
		const settings = gatedLinks.find((link) => link.id === "settings");

		// Settings group still renders (nodeSettings + External Providers + ungated Diagnostics); only the cloud
		// settings child is dropped — External Providers carries its own capability.
		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.nodeSettings,
			nodeRoutePaths.externalProviders,
			nodeRoutePaths.diagnostics,
		]);
		expect(settings?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.cloudSettings)).toBe(false);
	});

	it("hides External Providers and keeps the rest of the settings group when externalProviders is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ externalProviders: false });
		const settings = gatedLinks.find((link) => link.id === "settings");

		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.nodeSettings,
			nodeRoutePaths.cloudSettings,
			nodeRoutePaths.diagnostics,
		]);
	});

	it("groups Image Generation, Development Mode, Workflow Runs and Transcription under the Preview group as a pure toggle", () => {
		const preview = navigationLinks.find((link) => link.id === "preview");

		// Preview is a group (no own route); the experimental surfaces are its children.
		expect(preview?.to).toBeUndefined();
		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.images,
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
			nodeRoutePaths.transcription,
		]);
	});

	// C42: the module is "Development Workflows" but the NAV entry says "Workflow Runs", because it sits directly under
	// "Development" and two adjacent "Development…" entries are indistinguishable at a glance.
	it("labels the Development Workflows child so it does not read as Development Mode's sibling", () => {
		const preview = navigationLinks.find((link) => link.id === "preview");
		const child = preview?.links?.find((nestedLink) => nestedLink.to === nodeRoutePaths.devWorkflows);

		expect(child?.translationKey).toBe("navigation.devWorkflows");
	});

	it("drops the Image Generation child from Preview when the images capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ images: false });
		const preview = gatedLinks.find((link) => link.id === "preview");

		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
			nodeRoutePaths.transcription,
		]);
		// It must not reappear as a top-level entry either.
		expect(gatedLinks.some((link) => link.id === "images")).toBe(false);
	});

	it("drops the Workflow Runs child from Preview when the devWorkflows capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ devWorkflows: false });
		const preview = gatedLinks.find((link) => link.id === "preview");

		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.images,
			nodeRoutePaths.development,
			nodeRoutePaths.transcription,
		]);
	});

	// Graph Workflows ships ON since S4 and is a TOP-LEVEL entry, not a Preview child (R3): it replaced Open Canvas,
	// and a later refactor must not push it back into the group. The capability-off case is kept: the gate is still
	// the thing that decides, only its default moved.
	it("shows Graph Workflows by default as a top-level entry and drops it when the capability is off", async () => {
		const { navigationLinks: offLinks } = await mockCapabilities({ graphWorkflows: false });
		expect(offLinks.some((link) => link.id === "graphWorkflows")).toBe(false);

		const graphWorkflows = navigationLinks.find((link) => link.id === "graphWorkflows");
		const preview = navigationLinks.find((link) => link.id === "preview");

		// A top-level link with its own route, placed directly after the Preview group.
		expect(graphWorkflows?.to).toBe(nodeRoutePaths.graphWorkflows);
		expect(graphWorkflows?.links).toBeUndefined();
		expect(navigationLinks.map((link) => link.id).indexOf("graphWorkflows")).toBe(
			navigationLinks.map((link) => link.id).indexOf("preview") + 1,
		);
		// ...and NOT a child of the Preview group, which keeps exactly its own remaining children.
		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.images,
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
			nodeRoutePaths.transcription,
		]);
	});

	it("drops the Preview group entirely when every one of its children's capabilities is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({
			images: false,
			development: false,
			devWorkflows: false,
			transcription: false,
		});

		expect(gatedLinks.some((link) => link.id === "preview")).toBe(false);
	});

	it("hides the Benchmarks top-level entry when its capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ benchmarks: false });

		expect(gatedLinks.some((link) => link.id === "benchmarks")).toBe(false);
	});

	it("hides the Invocations top-level entry when its capability is off", async () => {
		expect(navigationLinks.some((link) => link.id === "invocations")).toBe(true);

		const { navigationLinks: gatedLinks } = await mockCapabilities({ invocationMonitor: false });

		expect(gatedLinks.some((link) => link.id === "invocations")).toBe(false);
	});

	it("drops the scheduler child from Automation when scheduler is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ scheduler: false });
		const automation = gatedLinks.find((link) => link.id === "automation");

		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.scheduler)).toBe(false);
		// Tools is ungated, so the group never collapses to empty.
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.tools)).toBe(true);
	});
});

describe("filterNavigationLinksByUiMode", () => {
	afterEach(() => {
		vi.resetModules();
		vi.doUnmock("@/capabilities/NodeCapabilities");
	});

	// The whole promise made to existing operators: turning the feature on changes nothing for them. Pinned against the
	// BUILDER's own output rather than a hand-copied list, so a future nav edit cannot drift the two apart silently.
	it("leaves the Advanced navigation byte-identical to the unfiltered, capability-filtered list", () => {
		expect(filterNavigationLinksByUiMode(navigationLinks, "advanced")).toEqual(navigationLinks);
	});

	it("shows only the everyday entries in Simple mode, with the one-child groups promoted away", () => {
		// Agents, Images and Transcription are TOP-LEVEL here: their groups (Automation, Preview) hold no other Simple
		// child, and a collapsible with one item inside — under an "Automation"/"Preview" heading that is itself an
		// Advanced idea — defeats the point of the mode.
		expect(filterNavigationLinksByUiMode(navigationLinks, "simple").map((link) => link.id)).toEqual([
			"home",
			"chat",
			"knowledgeBase",
			"models",
			"settings",
			"agents",
			"images",
			"transcription",
			"usage",
		]);
	});

	it("gives a promoted entry its own route, icon and label, and takes it out of its group", () => {
		const simpleLinks = filterNavigationLinksByUiMode(navigationLinks, "simple");

		const agents = simpleLinks.find((link) => link.id === "agents");
		expect(agents?.to).toBe(nodeRoutePaths.agents);
		expect(agents?.translationKey).toBe("navigation.agents");
		expect(agents?.links).toBeUndefined();
		expect(agents?.icon).toBeTruthy();

		expect(simpleLinks.find((link) => link.id === "images")?.to).toBe(nodeRoutePaths.images);
		expect(simpleLinks.find((link) => link.id === "transcription")?.to).toBe(nodeRoutePaths.transcription);

		// ...and the groups they came from are gone entirely, not rendered empty.
		expect(simpleLinks.some((link) => link.id === "automation")).toBe(false);
		expect(simpleLinks.some((link) => link.id === "preview")).toBe(false);
	});

	it("drops every group whose children are all Advanced, rather than rendering an empty expandable", () => {
		const simpleIds = filterNavigationLinksByUiMode(navigationLinks, "simple").map((link) => link.id);

		for (const groupId of ["automation", "integrations", "externalApps", "preview", "training"]) {
			expect(simpleIds).not.toContain(groupId);
		}
	});

	it("offers the AgentHome run history in Advanced mode only", () => {
		const targets = (mode: "simple" | "advanced"): (string | undefined)[] =>
			filterNavigationLinksByUiMode(navigationLinks, mode).flatMap((link) => [
				link.to,
				...(link.links ?? []).map((nestedLink) => nestedLink.to),
			]);

		// Operator forensics over an opt-in capability: a Simple-mode operator is never offered it, and never has it
		// promoted to a top-level entry either — the whole Automation group is gone in that mode.
		expect(targets("advanced")).toContain(nodeRoutePaths.agentRuns);
		expect(targets("simple")).not.toContain(nodeRoutePaths.agentRuns);
	});

	it("keeps a mixed group and hides only its Advanced children", () => {
		const settings = filterNavigationLinksByUiMode(navigationLinks, "simple").find((link) => link.id === "settings");

		// Diagnostics is Advanced-only in the nav; the header "Report Problem" button is its Simple-mode entry point.
		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.nodeSettings,
			nodeRoutePaths.cloudSettings,
			nodeRoutePaths.externalProviders,
		]);
	});

	it("keeps the Models group whole in Simple mode", () => {
		const models = filterNavigationLinksByUiMode(navigationLinks, "simple").find((link) => link.id === "models");

		expect(models?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.models,
			nodeRoutePaths.modelRecommendations,
			nodeRoutePaths.loadedModels,
		]);
	});

	// Capability is the FIRST gate and the mode never widens it: the mode filter runs on the already-capability-filtered
	// list, so a leaf compiled off is absent in both modes.
	it("cannot resurrect a leaf its capability compiled off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ knowledgeBase: false, images: false });

		for (const mode of ["simple", "advanced"] as const) {
			const ids = filterNavigationLinksByUiMode(gatedLinks, mode).map((link) => link.id);
			expect(ids).not.toContain("knowledgeBase");
			expect(ids).not.toContain("images");
		}
	});

	it("drops a promoted entry with its capability, and the emptied group with it", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ agentManagement: false });
		const simpleIds = filterNavigationLinksByUiMode(gatedLinks, "simple").map((link) => link.id);

		// Agents is the group's only Simple child, so with agentManagement off nothing survives — neither the promoted
		// entry nor an empty "Automation" group.
		expect(simpleIds).not.toContain("agents");
		expect(simpleIds).not.toContain("automation");
		// Advanced still shows the group, because its Advanced children are untouched by the capability.
		expect(filterNavigationLinksByUiMode(gatedLinks, "advanced").map((link) => link.id)).toContain("automation");
	});

	it("hides an Advanced leaf whose capability is on, in Simple mode only", () => {
		expect(filterNavigationLinksByUiMode(navigationLinks, "advanced").map((link) => link.id)).toContain("benchmarks");
		expect(filterNavigationLinksByUiMode(navigationLinks, "simple").map((link) => link.id)).not.toContain("benchmarks");
	});
});

// The guard the design asks for: a leaf added without a classification would otherwise default to Advanced-only and
// silently never appear in Simple mode. Walks `allNavigationLinks` — the list BEFORE the capability filter — so a leaf
// that ships behind an off-by-default capability is covered too.
describe("Simple/Advanced classification coverage", () => {
	it("classifies every leaf explicitly", () => {
		const unclassified: string[] = [];

		for (const link of allNavigationLinks) {
			if (link.links) {
				for (const nestedLink of link.links) {
					if (typeof nestedLink.simple !== "boolean") {
						unclassified.push(`${link.id} > ${nestedLink.translationKey}`);
					}
				}

				continue;
			}

			if (typeof link.simple !== "boolean") {
				unclassified.push(link.id);
			}
		}

		expect(unclassified).toEqual([]);
	});

	it("leaves the flag off group entries, whose children decide instead", () => {
		const groupsCarryingTheFlag = allNavigationLinks.filter((link) => link.links !== undefined && link.simple !== undefined);

		expect(groupsCarryingTheFlag.map((link) => link.id)).toEqual([]);
	});

	it("promotes only leaves that are in Simple mode", () => {
		const promotedButAdvanced = allNavigationLinks
			.flatMap((link) => link.links ?? [])
			.filter((nestedLink) => nestedLink.promoteInSimple !== undefined && !nestedLink.simple);

		expect(promotedButAdvanced.map((nestedLink) => nestedLink.translationKey)).toEqual([]);
	});
});

describe("matchesNavRoute", () => {
	it("matches an exact route and its sub-paths", () => {
		expect(matchesNavRoute("/models", "/models")).toBe(true);
		expect(matchesNavRoute("/models/abc123", "/models")).toBe(true);
	});

	it("does not match a sibling route that merely shares a prefix", () => {
		expect(matchesNavRoute("/models-extra", "/models")).toBe(false);
	});

	it("leaves a sub-path to the more specific entry when two entries share a prefix", () => {
		expect(matchesNavRoute("/training/datasets", "/training/datasets")).toBe(true);
		expect(matchesNavRoute("/training/datasets", "/training")).toBe(false);
		// A detail route no entry claims still highlights its parent.
		expect(matchesNavRoute("/training/run-1", "/training")).toBe(true);
	});

	it("matches the home route only exactly", () => {
		expect(matchesNavRoute("/", "/")).toBe(true);
		expect(matchesNavRoute("/models", "/")).toBe(false);
	});

	it("never matches an undefined target (a group toggle has no route)", () => {
		expect(matchesNavRoute("/anything", undefined)).toBe(false);
	});
});
