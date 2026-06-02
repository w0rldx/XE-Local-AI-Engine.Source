import { afterEach, describe, expect, it, vi } from "vitest";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { navigationLinks } from "@/data/navigation/NavigationMenuData";

describe("navigationLinks", () => {
	afterEach(() => {
		vi.resetModules();
		vi.doUnmock("@/capabilities/NodeCapabilities");
	});

	it("lists the node shell routes including the agents, mcp, scheduler, and model-fit links when their capabilities are on", () => {
		expect(navigationLinks.map((link) => [link.id, link.to])).toEqual([
			["home", nodeRoutePaths.home],
			["dashboard", nodeRoutePaths.dashboard],
			["chat", nodeRoutePaths.chat],
			["binding", nodeRoutePaths.binding],
			["node-settings", nodeRoutePaths.nodeSettings],
			["cloud-settings", nodeRoutePaths.cloudSettings],
			["models", nodeRoutePaths.models],
			["manager", nodeRoutePaths.manager],
			["invocations", nodeRoutePaths.invocations],
			["tools", nodeRoutePaths.tools],
			["agents", nodeRoutePaths.agents],
			["mcp", nodeRoutePaths.mcp],
			["scheduler", nodeRoutePaths.scheduler],
			["model-recommendations", nodeRoutePaths.modelRecommendations],
			["approved-images", nodeRoutePaths.approvedImages],
		]);
	});

	it("hides the agents link when agentManagement is off", async () => {
		vi.resetModules();
		vi.doMock("@/capabilities/NodeCapabilities", async () => {
			const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>(
				"@/capabilities/NodeCapabilities",
			);
			return {
				...actual,
				nodeCapabilities: { ...actual.nodeCapabilities, agentManagement: false },
			};
		});

		const { navigationLinks: gatedLinks } = await import("@/data/navigation/NavigationMenuData");
		expect(gatedLinks.some((link) => link.id === "agents")).toBe(false);
	});

	it("hides the mcp link when mcpServers is off", async () => {
		vi.resetModules();
		vi.doMock("@/capabilities/NodeCapabilities", async () => {
			const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>(
				"@/capabilities/NodeCapabilities",
			);
			return {
				...actual,
				nodeCapabilities: { ...actual.nodeCapabilities, mcpServers: false },
			};
		});

		const { navigationLinks: gatedLinks } = await import("@/data/navigation/NavigationMenuData");
		expect(gatedLinks.some((link) => link.id === "mcp")).toBe(false);
	});

	it("hides the scheduler link when scheduler is off", async () => {
		vi.resetModules();
		vi.doMock("@/capabilities/NodeCapabilities", async () => {
			const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>(
				"@/capabilities/NodeCapabilities",
			);
			return {
				...actual,
				nodeCapabilities: { ...actual.nodeCapabilities, scheduler: false },
			};
		});

		const { navigationLinks: gatedLinks } = await import("@/data/navigation/NavigationMenuData");
		expect(gatedLinks.some((link) => link.id === "scheduler")).toBe(false);
	});

	it("hides both model-fit links when modelFit is off", async () => {
		vi.resetModules();
		vi.doMock("@/capabilities/NodeCapabilities", async () => {
			const actual = await vi.importActual<typeof import("@/capabilities/NodeCapabilities")>(
				"@/capabilities/NodeCapabilities",
			);
			return {
				...actual,
				nodeCapabilities: { ...actual.nodeCapabilities, modelFit: false },
			};
		});

		const { navigationLinks: gatedLinks } = await import("@/data/navigation/NavigationMenuData");
		expect(gatedLinks.some((link) => link.id === "model-recommendations")).toBe(false);
		expect(gatedLinks.some((link) => link.id === "approved-images")).toBe(false);
	});
});
