import { describe, expect, it } from "vitest";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { navigationLinks } from "@/data/navigation/NavigationMenuData";

describe("navigationLinks", () => {
	it("lists the node shell routes", () => {
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
		]);
	});
});
