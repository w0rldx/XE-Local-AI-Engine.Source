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
	orchestrationTopologyJson: null,
};

const emptyTopology = { participantAgentDefinitionIds: [], handoffs: [], maxTurnsPerAgent: 8, returnToPrevious: false };

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
			orchestration: emptyTopology,
		});

		expect(request.name).toBe("Trimmed");
		expect(request.description).toBeNull();
		expect(request.instructions).toBe("Do things");
		expect(request.toolApprovals).toEqual({ GetCurrentTime: false });
	});

	it("sends a null topology for a Single definition even when an orchestration is present", () => {
		const request = toSaveAgentDefinitionRequest(
			{
				name: "Helper",
				description: "",
				instructions: "Help",
				modelProfile: null,
				reasoningEffort: null,
				kind: "Single",
				allowedToolNames: [],
				toolApprovals: {},
				orchestration: { ...emptyTopology, participantAgentDefinitionIds: ["spec-1"] },
			},
			"self-1",
		);

		expect(request.orchestrationTopologyJson).toBeNull();
	});

	it("serializes the topology for an Orchestrator definition into the request, pinning the triage to selfId", () => {
		const request = toSaveAgentDefinitionRequest(
			{
				name: "Coordinator",
				description: "",
				instructions: "Route",
				modelProfile: null,
				reasoningEffort: null,
				kind: "Orchestrator",
				allowedToolNames: [],
				toolApprovals: {},
				orchestration: {
					participantAgentDefinitionIds: ["spec-1", "spec-2"],
					handoffs: [{ fromAgentDefinitionId: "self-1", toAgentDefinitionId: "spec-1", reason: "  research  " }],
					maxTurnsPerAgent: 6,
					returnToPrevious: true,
				},
			},
			"self-1",
		);

		expect(request.orchestrationTopologyJson).not.toBeNull();
		const wire = JSON.parse(request.orchestrationTopologyJson as string);
		expect(wire.version).toBe(1);
		expect(wire.triageAgentDefinitionId).toBe("self-1");
		// The triage (self) is folded into participants at the head, deduped.
		expect(wire.participantAgentDefinitionIds).toEqual(["self-1", "spec-1", "spec-2"]);
		expect(wire.handoffs).toEqual([
			{ fromAgentDefinitionId: "self-1", toAgentDefinitionId: "spec-1", reason: "research" },
		]);
		expect(wire.maxTurnsPerAgent).toBe(6);
		expect(wire.returnToPrevious).toBe(true);
	});
});
