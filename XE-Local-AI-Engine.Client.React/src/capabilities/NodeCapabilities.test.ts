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

	it("enables the dedicated Development Mode surface", () => {
		expect(nodeCapabilities.development).toBe(true);
	});

	it("enables the model-fit surface by default", () => {
		expect(nodeCapabilities.modelFit).toBe(true);
	});

	it("enables the Open Canvas (preview) surface by default", () => {
		expect(nodeCapabilities.preview).toBe(true);
	});

	it("keeps node chat local-first and surfaces tool-approval controls", () => {
		expect(nodeCapabilities.chat).toEqual({
			localRuntime: true,
			localModelManagement: true,
			localTools: true,
			// The local tool-approval responder ships, so the chat surface exposes Approve/Deny controls.
			toolApprovals: true,
			conversationFeedback: true,
			offlineFirst: false,
			encryptedConversations: false,
			clientNodeRouting: false,
			fileAttachments: true,
			imageAttachments: true,
			agentManagement: true,
			voice: true,
			// Plain-chat knowledge-base grounding surface ships, so the composer exposes the "Use Knowledge Base" toggle.
			knowledgeBase: true,
		});
	});

	it("defines the route paths targeted by the node shell", () => {
			expect(nodeRoutePaths).toEqual({
			home: "/",
			chat: "/chat",
			development: "/development",
			knowledgeBase: "/knowledge-base",
			dashboard: "/dashboard",
			binding: "/node-binding",
			nodeSettings: "/node-settings",
			cloudSettings: "/cloud-settings",
			models: "/models",
			invocations: "/invocations",
			usage: "/usage",
			tools: "/tools",
			agents: "/agents",
			skills: "/skills",
			customTools: "/custom-tools",
			commands: "/commands",
			mcp: "/mcp",
			scheduler: "/scheduler",
			modelRecommendations: "/model-recommendations",
			loadedModels: "/loaded-models",
			preview: "/preview",
			images: "/images",
			diagnostics: "/diagnostics",
		});
	});
});
