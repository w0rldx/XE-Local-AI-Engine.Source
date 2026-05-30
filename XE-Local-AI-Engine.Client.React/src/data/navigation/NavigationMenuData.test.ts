import { afterEach, describe, expect, it, vi } from "vitest";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { navigationLinks } from "@/data/navigation/NavigationMenuData";

describe("navigationLinks", () => {
	afterEach(() => {
		vi.resetModules();
		vi.doUnmock("@/capabilities/NodeCapabilities");
	});

	it("lists the node shell routes including the agents and mcp links when their capabilities are on", () => {
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
});
