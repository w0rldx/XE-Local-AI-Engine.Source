// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated TanStack options factory so the hook runs against an owned queryFn (no network). The hook
// still composes the real withResponseValidation bridge + the real toFeedbackInsights select mapper, so this
// exercises the full read path end-to-end.
const { optionsMock } = vi.hoisted(() => ({ optionsMock: vi.fn() }));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAgentFeedbackInsightsOptions: optionsMock,
}));

import { useFeedbackInsights } from "@/features/agents/queries/useFeedbackInsights";

// A generated-shaped response (every field optional on the wire) the mocked options' queryFn resolves.
const generatedResponse = {
	agentDefinitionId: "agent-1",
	agentName: "Helper",
	generatedAtUtc: 1717000000000,
	minOccurrenceThreshold: 3,
	overall: { total: 10, up: 7, down: 3, downRate: 0.3, meetsThreshold: true },
	byTool: [{ toolName: "search", total: 4, up: 1, down: 3, downRate: 0.75, meetsThreshold: true }],
	exemplars: [
		{
			rating: "down",
			comment: "bad",
			messageId: "m1",
			conversationId: "c1",
			createdAtUtc: 1717000000001,
			truncated: false,
		},
	],
};

function makeWrapper() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("useFeedbackInsights", () => {
	beforeEach(() => {
		optionsMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "getAgentFeedbackInsights" }],
			queryFn: async () => generatedResponse,
		}));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("passes the agent id as a path param and maps the generated response into the domain insights", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useFeedbackInsights("agent-1"), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(optionsMock).toHaveBeenCalledWith({ path: { agentDefinitionId: "agent-1" } });
		expect(result.current.data).toEqual({
			agentDefinitionId: "agent-1",
			agentName: "Helper",
			generatedAtUtc: 1717000000000,
			minOccurrenceThreshold: 3,
			overall: { total: 10, up: 7, down: 3, downRate: 0.3, meetsThreshold: true },
			byTool: [{ toolName: "search", total: 4, up: 1, down: 3, downRate: 0.75, meetsThreshold: true }],
			exemplars: [
				{
					rating: "down",
					comment: "bad",
					messageId: "m1",
					conversationId: "c1",
					createdAtUtc: 1717000000001,
					truncated: false,
				},
			],
		});
	});

	it("is disabled (does not fetch) when no agent is selected", () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useFeedbackInsights(null), { wrapper: Wrapper });

		expect(result.current.fetchStatus).toBe("idle");
		expect(result.current.isPending).toBe(true);
	});
});
