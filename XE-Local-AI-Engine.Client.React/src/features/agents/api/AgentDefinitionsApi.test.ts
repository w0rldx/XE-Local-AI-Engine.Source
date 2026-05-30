import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		delete: vi.fn(),
		get: vi.fn(),
		post: vi.fn(),
		put: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import {
	type AgentDefinitionDto,
	createAgentDefinition,
	deleteAgentDefinition,
	listAgentDefinitions,
	listToolCapableModels,
	type SaveAgentDefinitionRequestDto,
	toSaveAgentDefinitionRequest,
	updateAgentDefinition,
} from "@/features/agents/api/AgentDefinitionsApi";

function makeDto(overrides: Partial<AgentDefinitionDto> = {}): AgentDefinitionDto {
	return {
		id: "agent-1",
		name: "Research assistant",
		description: "Helps with research",
		instructions: "You are a research assistant.",
		modelProfile: "qwen3:8b",
		reasoningEffort: "medium",
		kind: "Single",
		allowedToolNames: ["GetCurrentTime"],
		toolApprovals: { GetCurrentTime: true },
		orchestrationTopologyJson: null,
		version: 2,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

const sampleRequest: SaveAgentDefinitionRequestDto = {
	name: "Research assistant",
	description: "Helps with research",
	instructions: "You are a research assistant.",
	modelProfile: "qwen3:8b",
	reasoningEffort: "medium",
	kind: "Single",
	allowedToolNames: ["GetCurrentTime"],
	toolApprovals: { GetCurrentTime: true },
};

describe("agent definitions API", () => {
	it("lists agent definitions, mapping DTOs and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [makeDto()] } });

		const result = await listAgentDefinitions({ signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents", { signal: abortController.signal });
		expect(result).toHaveLength(1);
		expect(result[0]?.allowedToolNames).toEqual(["GetCurrentTime"]);
		expect(result[0]?.reasoningEffort).toBe("medium");
	});

	it("maps a null description to an empty string and unknown reasoning effort to null", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: { items: [makeDto({ description: null, reasoningEffort: "weird" })] },
		});

		const result = await listAgentDefinitions();

		expect(result[0]?.description).toBe("");
		expect(result[0]?.reasoningEffort).toBeNull();
	});

	it("creates an agent definition through POST", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto() });

		const result = await createAgentDefinition(sampleRequest);

		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents", sampleRequest, undefined);
		expect(result.id).toBe("agent-1");
	});

	it("updates an agent definition through PUT with an encoded id", async () => {
		axiosInstanceMock.put.mockResolvedValue({ data: makeDto({ id: "a/b" }) });

		await updateAgentDefinition("a/b", sampleRequest);

		expect(axiosInstanceMock.put).toHaveBeenCalledWith("/local/agents/a%2Fb", sampleRequest, undefined);
	});

	it("deletes an agent definition through DELETE with an encoded id", async () => {
		axiosInstanceMock.delete.mockResolvedValue({ data: undefined });

		await deleteAgentDefinition("agent-1");

		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/agents/agent-1", undefined);
	});

	it("reads the tool-capable model names", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: { models: ["qwen3:8b"] } });

		const result = await listToolCapableModels();

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/tool-capable-models", undefined);
		expect(result).toEqual(["qwen3:8b"]);
	});

	it("strips approvals for tools not in the allowed list when building a save request", () => {
		const request = toSaveAgentDefinitionRequest({
			name: "  Trimmed  ",
			description: "  ",
			instructions: "  Do things  ",
			modelProfile: "qwen3:8b",
			reasoningEffort: "low",
			kind: "Single",
			allowedToolNames: ["GetCurrentTime"],
			toolApprovals: { GetCurrentTime: false, Calculate: true },
		});

		expect(request.name).toBe("Trimmed");
		expect(request.description).toBeNull();
		expect(request.instructions).toBe("Do things");
		expect(request.toolApprovals).toEqual({ GetCurrentTime: false });
	});
});
