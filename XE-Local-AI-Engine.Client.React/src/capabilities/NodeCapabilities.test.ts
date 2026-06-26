import { describe, expect, it } from "vitest";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";

describe("nodeCapabilities", () => {
	it("enables agent management by default", () => {
		expect(nodeCapabilities.agentManagement).toBe(true);
	});

	it("enables MCP server management by default", () => {
		expect(nodeCapabilities.mcpServers).toBe(true);
	});

	it("enables the scheduler by default", () => {
		expect(nodeCapabilities.scheduler).toBe(true);
	});

	it("enables the model-fit surface by default", () => {
		expect(nodeCapabilities.modelFit).toBe(true);
	});

	it("enables the Open Canvas (preview) surface by default", () => {
		expect(nodeCapabilities.preview).toBe(true);
	});

	it("keeps node chat local-first and approval-gated for initial parity", () => {
		expect(nodeCapabilities.chat).toEqual({
			localRuntime: true,
			localModelManagement: true,
			localTools: true,
			toolApprovals: false,
			conversationFeedback: true,
			offlineFirst: false,
			encryptedConversations: false,
			clientNodeRouting: false,
			fileAttachments: true,
			imageAttachments: false,
			agentManagement: true,
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
			invocations: "/invocations",
			tools: "/tools",
			agents: "/agents",
			skills: "/skills",
			mcp: "/mcp",
			scheduler: "/scheduler",
			modelRecommendations: "/model-recommendations",
			loadedModels: "/loaded-models",
			preview: "/preview",
		});
	});
});
