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

	it("groups the related node pages under Models / Settings / Automation / Manager with flat entries around them", () => {
		expect(navigationLinks.map((link) => link.id)).toEqual([
			"home",
			"dashboard",
			"chat",
			"binding",
			"models",
			"settings",
			"automation",
			"preview",
			"manager",
			"invocations",
		]);
	});

	it("makes each group a pure toggle (no own route) with its children carrying the routes", () => {
		const models = navigationLinks.find((link) => link.id === "models");
		const settings = navigationLinks.find((link) => link.id === "settings");
		const automation = navigationLinks.find((link) => link.id === "automation");
		const manager = navigationLinks.find((link) => link.id === "manager");

		expect(models?.to).toBeUndefined();
		expect(settings?.to).toBeUndefined();
		expect(automation?.to).toBeUndefined();
		expect(manager?.to).toBeUndefined();

		expect(models?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.models,
			nodeRoutePaths.modelRecommendations,
			nodeRoutePaths.loadedModels,
		]);
		// cloudSettings capability is false in the default nodeCapabilities (local-only profile),
		// so the settings group shows only Node Settings; Cloud Settings is filtered out.
		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.nodeSettings]);
		expect(automation?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.agents,
			nodeRoutePaths.skills,
			nodeRoutePaths.mcp,
			nodeRoutePaths.scheduler,
			nodeRoutePaths.tools,
		]);
		// Manager group: runtime overview only (the approved-images page was removed with the approved-image concept).
		expect(manager?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.manager]);
	});

	it("keeps only Installed under Models and only Overview under Manager when modelFit and loadedModels are off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ modelFit: false, loadedModels: false });
		const models = gatedLinks.find((link) => link.id === "models");
		const manager = gatedLinks.find((link) => link.id === "manager");

		expect(models?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.models]);
		expect(manager?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.manager]);
	});

	it("drops the loaded-models child from Models when loadedModels is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ loadedModels: false });
		const models = gatedLinks.find((link) => link.id === "models");

		expect(models?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.loadedModels)).toBe(false);
		// Installed is ungated, so the group never collapses to empty.
		expect(models?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.models)).toBe(true);
	});

	it("drops the agents and skills children from Automation when agentManagement is off", async () => {
		// Both Agents and Skills are gated on agentManagement (skills are an agent-mode feature), so they drop together.
		const { navigationLinks: gatedLinks } = await mockCapabilities({ agentManagement: false });
		const automation = gatedLinks.find((link) => link.id === "automation");

		expect(automation?.links?.map((nestedLink) => nestedLink.to)).toEqual([
			nodeRoutePaths.mcp,
			nodeRoutePaths.scheduler,
			nodeRoutePaths.tools,
		]);
		expect(automation?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.skills)).toBe(false);
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
		]);
	});

	it("hides Cloud Settings and keeps Node Settings when cloudSettings capability is off", async () => {
		const { navigationLinks: gatedLinks } = await mockCapabilities({ cloudSettings: false });
		const settings = gatedLinks.find((link) => link.id === "settings");

		// Settings group still renders (nodeSettings is ungated); cloud settings child is dropped.
		expect(settings?.links?.map((nestedLink) => nestedLink.to)).toEqual([nodeRoutePaths.nodeSettings]);
		expect(settings?.links?.some((nestedLink) => nestedLink.to === nodeRoutePaths.cloudSettings)).toBe(false);
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
