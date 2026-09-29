import { describe, expect, it } from "vitest";

import { type AgentDefinition, isDefaultAssistantDefinition } from "@/features/agents/models/AgentDefinitionModels";

function definition(name: string, isDefaultAssistant: boolean): AgentDefinition {
	return {
		id: "agent-1",
		name,
		description: "",
		instructions: "Be helpful",
		modelProfile: null,
		reasoningEffort: null,
		kind: "Single",
		allowedToolNames: [],
		toolApprovals: {},
		allowedSkillIds: [],
		orchestrationTopologyJson: null,
		playbookEnabled: false,
		defaultTemporaryChat: false,
		memoryExtractionEnabled: true,
		disableBaseScaffold: false,
		disableToolRelevanceFilter: false,
		isDefaultAssistant,
		version: 1,
		createdAtUtc: 0,
		updatedAtUtc: 0,
	};
}

describe("isDefaultAssistantDefinition", () => {
	it("follows the server's provenance flag", () => {
		expect(isDefaultAssistantDefinition(definition("Default Assistant", true))).toBe(true);
	});

	// An operator agent that merely shares the seeded name is not the Default Assistant.
	it("never matches on the name alone", () => {
		expect(isDefaultAssistantDefinition(definition("Default Assistant", false))).toBe(false);
	});
});
