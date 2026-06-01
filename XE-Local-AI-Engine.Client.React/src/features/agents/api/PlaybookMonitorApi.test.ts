import { describe, expect, it, vi } from "vitest";

const { axiosInstanceMock, buildLocalApiUrlMock } = vi.hoisted(() => ({
	axiosInstanceMock: {
		get: vi.fn(),
	},
	buildLocalApiUrlMock: vi.fn((path: string) => `/local/${path}`),
}));

vi.mock("@/core/api/axios/AxiosInstance", () => ({
	axiosInstance: axiosInstanceMock,
}));

vi.mock("@/core/api/utils/LocalApiUrl", () => ({
	buildLocalApiUrl: buildLocalApiUrlMock,
}));

import { getPlaybookMonitor } from "@/features/agents/api/PlaybookMonitorApi";
import type { PlaybookMonitorDto } from "@/features/agents/models/PlaybookMonitorModels";

function makeResponse(overrides: Partial<PlaybookMonitorDto> = {}): PlaybookMonitorDto {
	return {
		items: [
			{
				actionId: "action-1",
				enabledAtUtc: 1700,
				beforeDownRate: 0.12,
				afterDownRate: 0.05,
				afterSampleSize: 8,
				status: "Improved",
				flagged: false,
				facetToolName: "search",
			},
		],
		retrieval: { threshold: 8, topK: 8, ranker: "lexical", embeddingModel: null },
		...overrides,
	};
}

describe("playbook monitor API", () => {
	it("fetches the per-agent monitor view, building the GET URL and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: makeResponse() });

		const result = await getPlaybookMonitor("agent-1", { signal: abortController.signal });

		expect(axiosInstanceMock.get.mock.calls.at(-1)?.at(0)).toBe("/local/agents/agent-1/playbook/monitor");
		expect(axiosInstanceMock.get.mock.calls.at(-1)?.at(1)).toEqual({ signal: abortController.signal });
		expect(result.items).toHaveLength(1);
		expect(result.items[0]?.actionId).toBe("action-1");
		expect(result.items[0]?.status).toBe("Improved");
		expect(result.items[0]?.beforeDownRate).toBeCloseTo(0.12);
		expect(result.items[0]?.afterDownRate).toBeCloseTo(0.05);
		expect(result.items[0]?.flagged).toBe(false);
		expect(result.items[0]?.facetToolName).toBe("search");
		expect(result.retrieval.threshold).toBe(8);
		expect(result.retrieval.topK).toBe(8);
		// Lexical default: ranker surfaces and the embedding model normalizes to null.
		expect(result.retrieval.ranker).toBe("lexical");
		expect(result.retrieval.embeddingModel).toBeNull();
	});

	it("carries the embedding ranker and model through the boundary when embeddings are active", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: makeResponse({ retrieval: { threshold: 8, topK: 8, ranker: "embedding", embeddingModel: "nomic-embed-text" } }),
		});

		const result = await getPlaybookMonitor("agent-1");

		expect(result.retrieval.ranker).toBe("embedding");
		expect(result.retrieval.embeddingModel).toBe("nomic-embed-text");
	});

	it("normalizes an omitted embeddingModel to null (lexical path without the field)", async () => {
		// The host omits embeddingModel entirely on the lexical path; the boundary normalizes it to null.
		axiosInstanceMock.get.mockResolvedValue({
			data: makeResponse({ retrieval: { threshold: 8, topK: 8, ranker: "lexical" } as PlaybookMonitorDto["retrieval"] }),
		});

		const result = await getPlaybookMonitor("agent-1");

		expect(result.retrieval.ranker).toBe("lexical");
		expect(result.retrieval.embeddingModel).toBeNull();
	});

	it("throws when the ranker is not one of the known lowercase literals", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: makeResponse({
				retrieval: {
					threshold: 8,
					topK: 8,
					ranker: "Embedding" as PlaybookMonitorDto["retrieval"]["ranker"],
					embeddingModel: null,
				},
			}),
		});

		await expect(getPlaybookMonitor("agent-1")).rejects.toThrow(/Invalid playbook monitor payload/);
	});

	it("normalizes a null facet tool name (an action without a tool scope)", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: makeResponse({
				items: [
					{
						actionId: "action-2",
						enabledAtUtc: 100,
						beforeDownRate: 0,
						afterDownRate: 0,
						afterSampleSize: 1,
						status: "InsufficientData",
						flagged: false,
						facetToolName: null,
					},
				],
			}),
		});

		const result = await getPlaybookMonitor("agent-1");

		expect(result.items[0]?.facetToolName).toBeNull();
		expect(result.items[0]?.status).toBe("InsufficientData");
	});

	it("encodes the agent id into the route", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: makeResponse() });

		await getPlaybookMonitor("ag/1");

		expect(axiosInstanceMock.get.mock.calls.at(-1)?.at(0)).toBe("/local/agents/ag%2F1/playbook/monitor");
	});

	it("throws when the payload does not match the contract", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: { items: [{ actionId: "action-1" }] } });

		await expect(getPlaybookMonitor("agent-1")).rejects.toThrow(/Invalid playbook monitor payload/);
	});

	it("throws when the status is not one of the four known verdicts", async () => {
		axiosInstanceMock.get.mockResolvedValue({
			data: makeResponse({
				items: [
					{
						actionId: "action-1",
						enabledAtUtc: 1,
						beforeDownRate: 0,
						afterDownRate: 0,
						afterSampleSize: 0,
						status: "Unknown" as PlaybookMonitorDto["items"][number]["status"],
						flagged: false,
						facetToolName: null,
					},
				],
			}),
		});

		await expect(getPlaybookMonitor("agent-1")).rejects.toThrow(/Invalid playbook monitor payload/);
	});
});
