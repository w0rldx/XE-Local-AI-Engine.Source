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

	it("enables local benchmarks by default", () => {
		expect(nodeCapabilities.benchmarks).toBe(true);
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

	it("enables the external-provider surface by default", () => {
		expect(nodeCapabilities.externalProviders).toBe(true);
	});

	it("enables the work-session surface by default", () => {
		expect(nodeCapabilities.workSessions).toBe(true);
	});

	it("enables the Development Workflows surface by default", () => {
		expect(nodeCapabilities.devWorkflows).toBe(true);
	});

	it("enables the External Integrations surface by default", () => {
		expect(nodeCapabilities.integrations).toBe(true);
	});

	it("defines the route paths targeted by the node shell", () => {
		expect(nodeRoutePaths).toEqual({
			home: "/",
			chat: "/chat",
			development: "/development",
			workSessions: "/work-sessions",
			devWorkflows: "/development-workflows",
			knowledgeBase: "/knowledge-base",
			dashboard: "/dashboard",
			binding: "/node-binding",
			nodeSettings: "/node-settings",
			cloudSettings: "/cloud-settings",
			externalProviders: "/external-providers",
			models: "/models",
			invocations: "/invocations",
			benchmarks: "/benchmarks",
			training: "/training",
			trainingDatasets: "/training/datasets",
			trainingComparisons: "/training/comparisons",
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
			integrationTriggers: "/integrations/triggers",
			integrationSessions: "/integrations/sessions",
			integrationExecutions: "/integrations/executions",
			integrationKeys: "/integrations/keys",
			diagnostics: "/diagnostics",
		});
	});
});
