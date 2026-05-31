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

import { getFeedbackInsights } from "@/features/agents/api/FeedbackInsightsApi";
import type { FeedbackInsightsDto } from "@/features/agents/models/FeedbackInsightsModels";

function makeResponse(overrides: Partial<FeedbackInsightsDto> = {}): FeedbackInsightsDto {
	return {
		agentDefinitionId: "agent-1",
		agentName: "Researcher",
		generatedAtUtc: 1700,
		minOccurrenceThreshold: 3,
		overall: { total: 5, up: 3, down: 2, downRate: 0.4, meetsThreshold: true },
		byTool: [{ toolName: "search", total: 4, up: 3, down: 1, downRate: 0.25, meetsThreshold: true }],
		exemplars: [
			{
				rating: "down",
				comment: "Too slow",
				messageId: "msg-1",
				conversationId: "conv-1",
				createdAtUtc: 1500,
				truncated: false,
			},
		],
		...overrides,
	};
}

describe("feedback insights API", () => {
	it("fetches the per-agent insights, building the GET URL and forwarding the abort signal", async () => {
		const abortController = new AbortController();
		axiosInstanceMock.get.mockResolvedValue({ data: makeResponse() });

		const result = await getFeedbackInsights("agent-1", { signal: abortController.signal });

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/agent-1/feedback-insights", {
			signal: abortController.signal,
		});
		expect(result.agentName).toBe("Researcher");
		expect(result.overall.down).toBe(2);
		expect(result.overall.downRate).toBeCloseTo(0.4);
		expect(result.byTool[0]?.toolName).toBe("search");
		expect(result.exemplars[0]?.rating).toBe("down");
		expect(result.exemplars[0]?.comment).toBe("Too slow");
	});

	it("encodes the agent id into the route", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: makeResponse({ agentDefinitionId: "ag/1" }) });

		await getFeedbackInsights("ag/1");

		expect(axiosInstanceMock.get).toHaveBeenCalledWith("/local/agents/ag%2F1/feedback-insights", undefined);
	});

	it("throws when the payload does not match the contract", async () => {
		axiosInstanceMock.get.mockResolvedValue({ data: { agentDefinitionId: "agent-1" } });

		await expect(getFeedbackInsights("agent-1")).rejects.toThrow(/Invalid feedback insights payload/);
	});
});
