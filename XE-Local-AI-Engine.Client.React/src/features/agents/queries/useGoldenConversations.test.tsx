// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated TanStack module so the hooks run against owned query/mutation fns (no network). The hooks still
// compose the real withResponseValidation bridge + the real mappers, so the read path is exercised end-to-end and
// each mutation's dispatched wire envelope can be asserted. listGoldenConversationsOptions returns a hey-api options
// object ({ queryKey, queryFn }); each *Mutation returns an object whose mutationFn the hook dispatches into.
const { listMock, createMutationFn, deleteMutationFn, harvestMutationFn, approveMutationFn } = vi.hoisted(() => ({
	listMock: vi.fn(),
	createMutationFn: vi.fn(),
	deleteMutationFn: vi.fn(),
	harvestMutationFn: vi.fn(),
	approveMutationFn: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listGoldenConversationsOptions: listMock,
	createGoldenConversationMutation: vi.fn(() => ({ mutationFn: createMutationFn })),
	deleteGoldenConversationMutation: vi.fn(() => ({ mutationFn: deleteMutationFn })),
	harvestGoldenConversationsMutation: vi.fn(() => ({ mutationFn: harvestMutationFn })),
	approveGoldenConversationMutation: vi.fn(() => ({ mutationFn: approveMutationFn })),
}));

import {
	goldenConversationsInvalidationKey,
	goldenConversationsQueryIds,
	useApproveGolden,
	useCreateGoldenConversation,
	useDeleteGoldenConversation,
	useGoldenConversations,
	useHarvestGolden,
} from "@/features/agents/queries/useGoldenConversations";

// The generated query key the mutations invalidate (partial `_id` match), built via the production helper.
const LIST_KEY = goldenConversationsInvalidationKey(goldenConversationsQueryIds.list);

// A generated-shaped list response (every field optional on the wire) the mocked options' queryFn resolves.
const generatedListResponse = {
	items: [
		{
			id: "g-1",
			agentDefinitionId: "agent-1",
			title: "Summarizes",
			inputTurns: [{ role: "user", text: "Summarize" }],
			assertion: { requiredPhrases: ["summary"], forbiddenPhrases: [] },
			rubric: null,
			enabled: true,
			source: "manual",
			sourceMessageId: null,
			sourceConversationId: null,
			createdAtUtc: 1,
			updatedAtUtc: 2,
		},
	],
};

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("useGoldenConversations (read)", () => {
	beforeEach(() => {
		listMock.mockImplementation(() => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: goldenConversationsQueryIds.list }],
			queryFn: async () => generatedListResponse,
		}));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("passes the agent id as a path param and maps the generated response into domain cases", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useGoldenConversations("agent-1"), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(listMock).toHaveBeenCalledWith({ path: { agentDefinitionId: "agent-1" } });
		expect(result.current.data).toHaveLength(1);
		expect(result.current.data?.[0]).toEqual({
			id: "g-1",
			agentDefinitionId: "agent-1",
			title: "Summarizes",
			inputTurns: [{ role: "user", text: "Summarize" }],
			assertion: { requiredPhrases: ["summary"], forbiddenPhrases: [] },
			rubric: null,
			enabled: true,
			source: "manual",
			sourceMessageId: null,
			sourceConversationId: null,
			createdAtUtc: 1,
			updatedAtUtc: 2,
		});
	});

	it("is disabled (does not fetch) when no agent is selected", () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useGoldenConversations(null), { wrapper: Wrapper });

		expect(result.current.fetchStatus).toBe("idle");
		expect(result.current.isPending).toBe(true);
	});
});

describe("golden conversation mutations", () => {
	beforeEach(() => {
		createMutationFn.mockResolvedValue({ id: "g-new" });
		deleteMutationFn.mockResolvedValue(undefined);
		harvestMutationFn.mockResolvedValue({
			thumbsUpScanned: 4,
			createdCount: 2,
			duplicateCount: 1,
			skippedCount: 1,
		});
		approveMutationFn.mockResolvedValue({ id: "g-1" });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create dispatches the domain request into the generated { path, body } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateGoldenConversation("agent-1"), { wrapper: Wrapper });

		result.current.mutate({ title: "T", inputTurns: [{ role: "user", text: "hi" }], rubric: "ok" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 calls mutationFn(variables, context) — assert variables via the first call arg only.
		// toCreateGoldenConversationRequest projects the domain request onto the generated body: readonly arrays
		// become mutable and an absent assertion is sent as explicit null.
		expect(createMutationFn.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1" },
			body: { title: "T", inputTurns: [{ role: "user", text: "hi" }], assertion: null, rubric: "ok" },
		});
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("delete dispatches the golden id into the generated { path } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteGoldenConversation("agent-1"), { wrapper: Wrapper });

		result.current.mutate("g-7");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(deleteMutationFn.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", goldenConversationId: "g-7" },
		});
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("harvest dispatches the bound { path } envelope, maps the counts, and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useHarvestGolden("agent-1"), { wrapper: Wrapper });

		result.current.mutate();

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(harvestMutationFn.mock.calls[0]?.[0]).toEqual({ path: { agentDefinitionId: "agent-1" } });
		expect(result.current.data).toEqual({
			thumbsUpScanned: 4,
			createdCount: 2,
			duplicateCount: 1,
			skippedCount: 1,
		});
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("approve dispatches the golden id into the generated { path } envelope and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useApproveGolden("agent-1"), { wrapper: Wrapper });

		result.current.mutate("g-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(approveMutationFn.mock.calls[0]?.[0]).toEqual({
			path: { agentDefinitionId: "agent-1", goldenConversationId: "g-1" },
		});
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("surfaces a mutation error and does not invalidate", async () => {
		createMutationFn.mockRejectedValue(new Error("Request failed with status code 400"));
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateGoldenConversation("agent-1"), { wrapper: Wrapper });

		result.current.mutate({ title: "T", inputTurns: [{ role: "user", text: "hi" }], rubric: "ok" });

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(LIST_KEY);
	});
});
