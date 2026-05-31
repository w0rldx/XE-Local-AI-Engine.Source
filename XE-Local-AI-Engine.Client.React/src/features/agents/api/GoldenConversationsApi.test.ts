import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		delete: vi.fn(),
		get: vi.fn(),
		post: vi.fn(),
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
	createGoldenConversation,
	deleteGoldenConversation,
	listGoldenConversations,
} from "@/features/agents/api/GoldenConversationsApi";
import type {
	CreateGoldenConversationRequestDto,
	GoldenConversationDto,
} from "@/features/agents/models/GoldenConversationModels";

function makeDto(overrides: Partial<GoldenConversationDto> = {}): GoldenConversationDto {
	return {
		id: "golden-1",
		agentDefinitionId: "agent-1",
		title: "Summarizes accurately",
		inputTurns: [{ role: "user", text: "Summarize the document" }],
		assertion: { requiredPhrases: ["summary"], forbiddenPhrases: ["error"] },
		rubric: null,
		enabled: true,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		...overrides,
	};
}

describe("golden conversations API", () => {
	it("lists golden cases, mapping the envelope and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [makeDto()] } });

		const result = await listGoldenConversations("agent-1", { signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/agent-1/golden-conversations", {
			signal: abortController.signal,
		});
		expect(result).toHaveLength(1);
		expect(result[0]?.title).toBe("Summarizes accurately");
		expect(result[0]?.inputTurns).toHaveLength(1);
		expect(result[0]?.assertion?.requiredPhrases).toEqual(["summary"]);
		expect(result[0]?.rubric).toBeNull();
	});

	it("normalizes a missing assertion/rubric to null", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: { items: [makeDto({ assertion: null, rubric: "Judge: is the summary accurate?" })] },
		});

		const result = await listGoldenConversations("agent-1");

		expect(result[0]?.assertion).toBeNull();
		expect(result[0]?.rubric).toBe("Judge: is the summary accurate?");
	});

	it("throws when the list payload does not match the contract", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [{ id: "golden-1" }] } });

		await expect(listGoldenConversations("agent-1")).rejects.toThrow(/Invalid golden conversations payload/);
	});

	it("creates a golden case through POST and maps the bare response", async () => {
		const request: CreateGoldenConversationRequestDto = {
			title: "Cites sources",
			inputTurns: [{ role: "user", text: "What is the capital of France?" }],
			assertion: { requiredPhrases: ["Paris"], forbiddenPhrases: [] },
		};
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto({ id: "golden-2", title: "Cites sources" }) });

		const result = await createGoldenConversation("agent-1", request);

		expect(axiosInstanceMock.post).toHaveBeenCalledWith("/local/agents/agent-1/golden-conversations", request, undefined);
		expect(result.id).toBe("golden-2");
		expect(result.title).toBe("Cites sources");
	});

	it("encodes the agent id into the create route", async () => {
		axiosInstanceMock.post.mockResolvedValue({ data: makeDto({ agentDefinitionId: "ag/1" }) });

		await createGoldenConversation("ag/1", { title: "t", inputTurns: [{ role: "user", text: "hi" }], rubric: "r" });

		expect(axiosInstanceMock.post.mock.calls.at(-1)?.at(0)).toBe("/local/agents/ag%2F1/golden-conversations");
	});

	it("deletes a golden case through DELETE with encoded ids", async () => {
		axiosInstanceMock.delete.mockResolvedValue({ data: undefined });

		await deleteGoldenConversation("ag/1", "g/2");

		expect(axiosInstanceMock.delete).toHaveBeenCalledWith("/local/agents/ag%2F1/golden-conversations/g%2F2", undefined);
	});
});
