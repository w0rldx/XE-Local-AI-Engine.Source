// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated TanStack options factory so the hook runs against an owned queryFn (no network). The hook
// still composes the real withResponseValidation bridge + the real toPlaybookMonitor select mapper, so this
// exercises the full read path end-to-end (incl. the ranker narrowing + facetToolName null defaults).
const { optionsMock } = vi.hoisted(() => ({ optionsMock: vi.fn() }));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAgentPlaybookMonitorOptions: optionsMock,
}));

import { usePlaybookMonitor } from "@/features/agents/queries/usePlaybookMonitor";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const generatedResponse = {
	items: [
		{
			actionId: "act-1",
			enabledAtUtc: 1717000000000,
			beforeDownRate: 0.4,
			afterDownRate: 0.1,
			afterSampleSize: 12,
			status: "Improved",
			flagged: false,
			facetToolName: null,
		},
	],
	retrieval: { threshold: 5, topK: 3, ranker: "embedding", embeddingModel: "nomic-embed-text" },
};

function makeWrapper() {
	const { wrapper } = createProvidersWrapper();
	return { wrapper };
}

describe("usePlaybookMonitor", () => {
	beforeEach(() => {
		optionsMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "getAgentPlaybookMonitor" }],
			queryFn: async () => generatedResponse,
		}));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("passes the agent id as a path param and maps items + the embedding retrieval config", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => usePlaybookMonitor("agent-1"), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(optionsMock).toHaveBeenCalledWith({ path: { agentDefinitionId: "agent-1" } });
		expect(result.current.data).toEqual({
			items: [
				{
					actionId: "act-1",
					enabledAtUtc: 1717000000000,
					beforeDownRate: 0.4,
					afterDownRate: 0.1,
					afterSampleSize: 12,
					status: "Improved",
					flagged: false,
					facetToolName: null,
				},
			],
			retrieval: { threshold: 5, topK: 3, ranker: "embedding", embeddingModel: "nomic-embed-text" },
		});
	});

	it("is disabled (does not fetch) when no agent is selected", () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => usePlaybookMonitor(null), { wrapper });

		expect(result.current.fetchStatus).toBe("idle");
		expect(result.current.isPending).toBe(true);
	});
});
