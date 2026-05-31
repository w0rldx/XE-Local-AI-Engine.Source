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
	createPlaybookAction,
	deletePlaybookAction,
	listPlaybookActions,
	updatePlaybookAction,
} from "@/features/agents/api/PlaybookActionsApi";
import type { PlaybookActionDto, SavePlaybookActionRequestDto } from "@/features/agents/models/PlaybookActionModels";

function makeDto(overrides: Partial<PlaybookActionDto> = {}): PlaybookActionDto {
	return {
		id: "action-1",
		agentDefinitionId: "agent-1",
		state: "Enabled",
		source: "Manual",
		triggerCondition: "When asked for code",
		behavior: "Always include tests",
		scope: "coding",
		priority: 0,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

const sampleRequest: SavePlaybookActionRequestDto = {
	state: "Enabled",
	triggerCondition: "When asked for code",
	behavior: "Always include tests",
	scope: "coding",
	priority: 0,
};

describe("playbook actions API", () => {
	it("lists playbook actions for an agent, mapping DTOs and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [makeDto()] } });

		const result = await listPlaybookActions("agent-1", { signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/agent-1/playbook", {
			signal: abortController.signal,
		});
		expect(result).toHaveLength(1);
		expect(result[0]?.behavior).toBe("Always include tests");
		expect(result[0]?.state).toBe("Enabled");
		expect(result[0]?.source).toBe("Manual");
	});

	it("maps a null trigger/scope and an unknown state/source defensively", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: { items: [makeDto({ triggerCondition: null, scope: null, state: "weird", source: "weird" })] },
		});

		const result = await listPlaybookActions("agent-1");

		expect(result[0]?.triggerCondition).toBeNull();
		expect(result[0]?.scope).toBeNull();
		// Unknown state falls back to Disabled (the non-injecting state); unknown source falls back to Manual.
		expect(result[0]?.state).toBe("Disabled");
		expect(result[0]?.source).toBe("Manual");
	});

	it("creates a playbook action through POST under the agent route", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto() });

		const result = await createPlaybookAction("agent-1", sampleRequest);

		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/agent-1/playbook", sampleRequest, undefined);
		expect(result.id).toBe("action-1");
	});

	it("updates a playbook action through PUT with encoded ids", async () => {
		axiosInstanceMock.put.mockResolvedValue({ data: makeDto({ id: "a/b" }) });

		await updatePlaybookAction("ag/1", "a/b", sampleRequest);

		expect(axiosInstanceMock.put).toHaveBeenCalledWith("/local/agents/ag%2F1/playbook/a%2Fb", sampleRequest, undefined);
	});

	it("deletes a playbook action through DELETE under the agent route", async () => {
		axiosInstanceMock.delete.mockResolvedValue({ data: undefined });

		await deletePlaybookAction("agent-1", "action-1");

		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/agents/agent-1/playbook/action-1", undefined);
	});
});
