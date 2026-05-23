import { describe, expect, it } from "vitest";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

describe("nodeCapabilities", () => {
	it("keeps node chat local-first and approval-gated for initial parity", () => {
		expect(nodeCapabilities.chat).toEqual({
			localRuntime: true,
			localModelManagement: true,
			localTools: true,
			toolApprovals: false,
			conversationFeedback: false,
			offlineFirst: true,
			encryptedConversations: false,
			clientNodeRouting: false,
			fileAttachments: false,
			imageAttachments: false,
		});
	});

	it("defines the route paths targeted by the node shell", () => {
		expect(nodeRoutePaths).toEqual({
			home: "/",
			chat: "/chat",
			dashboard: "/dashboard",
			binding: "/node-binding",
			nodeSettings: "/node-settings",
			cloudSettings: "/cloud-settings",
			models: "/models",
			manager: "/manager",
			invocations: "/invocations",
		});
	});
});
