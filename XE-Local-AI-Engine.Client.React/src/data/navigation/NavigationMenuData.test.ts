import { afterEach, describe, expect, it, vi } from "vitest";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { matchesNavRoute, navigationLinks } from "@/data/navigation/NavigationMenuData";

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
		// dashboard + binding are Central-Platform surfaces gated off in the default (local-only) profile, so they are
		// filtered out of the top-level entries here — see the dedicated test below for when their capabilities are on.
		expect(navigationLinks.map((link) => link.id)).toEqual([
			"home",
			"chat",
			"knowledgeBase",
			"models",
			"settings",
			"automation",
			"integrations",
			"preview",
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

	it("shows Dashboard and Node Binding as top-level entries when their Central-Platform capabilities are on", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ dashboard: true, binding: true });

		expect(gatedLinks.map((link) => link.id)).toEqual([
			"home",
			"dashboard",
			"chat",
			"knowledgeBase",
			"binding",
			"models",
			"settings",
			"automation",
			"integrations",
			"preview",
			"benchmarks",
			"training",
			"invocations",
			"usage",
		]);
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

	it("hides Dashboard and Node Binding in the local-only profile (capabilities off)", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ dashboard: false, binding: false });

		expect(gatedLinks.some((link) => link.id === "dashboard")).toBe(false);
		expect(gatedLinks.some((link) => link.id === "binding")).toBe(false);
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

	it("groups Open Canvas, Image Generation, Development Mode and Workflow Runs under the Preview group as a pure toggle", () => {
		const preview = navigationLinks.find((link) => link.id === "preview");

		// Preview is a group (no own route); the four experimental surfaces are its children.
		expect(preview?.to).toBeUndefined();
		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.preview,
			nodeRoutePaths.images,
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
		]);
	});

	// C42: the module is "Development Workflows" but the NAV entry says "Workflow Runs", because it sits directly under
	// "Development" and two adjacent "Development…" entries are indistinguishable at a glance.
	it("labels the Development Workflows child so it does not read as Development Mode's sibling", () => {
		const preview = navigationLinks.find((link) => link.id === "preview");
		const child = preview?.links?.find((nestedLink) => nestedLink.to === nodeRoutePaths.devWorkflows);

		expect(child?.translationKey).toBe("navigation.devWorkflows");
	});

	it("keeps the other preview children when the preview (Open Canvas) capability is off", async () => {
		// The group itself is ungated — each child carries its own capability — so turning Open Canvas off leaves the
		// Preview group standing with its remaining children.
		const { navigationLinks: gatedLinks } = await mockCapabilities({ preview: false });
		const preview = gatedLinks.find((link) => link.id === "preview");

		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.images,
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
		]);
	});

	it("drops the Image Generation child from Preview when the images capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ images: false });
		const preview = gatedLinks.find((link) => link.id === "preview");

		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.preview,
			nodeRoutePaths.development,
			nodeRoutePaths.devWorkflows,
		]);
		// It must not reappear as a top-level entry either.
		expect(gatedLinks.some((link) => link.id === "images")).toBe(false);
	});

	it("drops the Workflow Runs child from Preview when the devWorkflows capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ devWorkflows: false });
		const preview = gatedLinks.find((link) => link.id === "preview");

		expect(preview?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.preview,
			nodeRoutePaths.images,
			nodeRoutePaths.development,
		]);
	});

	it("drops the Preview group entirely when every one of its children's capabilities is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({
			preview: false,
			images: false,
			development: false,
			devWorkflows: false,
		});

		expect(gatedLinks.some((link) => link.id === "preview")).toBe(false);
	});

	it("hides the Benchmarks top-level entry when its capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ benchmarks: false });

		expect(gatedLinks.some((link) => link.id === "benchmarks")).toBe(false);
	});

	it("drops the scheduler child from Automation when scheduler is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ scheduler: false });
		const automation = gatedLinks.find((link) => link.id === "automation");

		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.scheduler)).toBe(false);
		// Tools is ungated, so the group never collapses to empty.
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.tools)).toBe(true);
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

	it("matches the home route only exactly", () => {
		expect(matchesNavRoute("/", "/")).toBe(true);
		expect(matchesNavRoute("/models", "/")).toBe(false);
	});

	it("never matches an undefined target (a group toggle has no route)", () => {
		expect(matchesNavRoute("/anything", undefined)).toBe(false);
	});
});
