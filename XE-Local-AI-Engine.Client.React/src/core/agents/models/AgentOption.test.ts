import { describe, expect, it } from "vitest";

import type { AgentDefinition } from "@/core/agents/models/AgentDefinitionModels";
import { toAgentOptions } from "@/core/agents/models/AgentOption";

function definition(id: string, name: string): AgentDefinition {
	return {
		id,
		name,
		description: `${name} description`,
		instructions: "Be helpful",
		modelProfile: null,
		reasoningEffort: null,
		kind: "Single",
		allowedToolNames: [],
		toolApprovals: {},
		allowedSkillIds: [],
		orchestrationTopologyJson: null,
		playbookEnabled: id === "b",
		defaultTemporaryChat: false,
		memoryExtractionEnabled: true,
		disableBaseScaffold: false,
		disableToolRelevanceFilter: false,
		isDefaultAssistant: false,
		version: 1,
		createdAtUtc: 0,
		updatedAtUtc: 0,
	};
}

describe("toAgentOptions", () => {
	it("drops the Default Assistant by name, case-insensitively, and sorts the rest by name", () => {
		const options = toAgentOptions([definition("z", "Zeta"), definition("d", "default assistant"), definition("b", "Beta")]);

		expect(options).toEqual([
			{ id: "b", name: "Beta", description: "Beta description", kind: "Single", modelProfile: null, playbookEnabled: true },
			{ id: "z", name: "Zeta", description: "Zeta description", kind: "Single", modelProfile: null, playbookEnabled: false },
		]);
	});
});
