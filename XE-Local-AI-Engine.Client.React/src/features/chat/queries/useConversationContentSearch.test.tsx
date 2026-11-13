// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const { searchSpy } = vi.hoisted(() => ({ searchSpy: vi.fn() }));

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: { searchConversations: searchSpy },
}));

import { useConversationContentSearch } from "@/features/chat/queries/useConversationContentSearch";

// The hook's re-ask period while the node reports it is still indexing.
const INDEXING_REFETCH_MS = 5_000;
import { createProvidersWrapper } from "@/test/RenderWithProviders";

describe("useConversationContentSearch", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("searches the trimmed query once typing settles and returns the node's answer", async () => {
		const answer = { conversations: [], indexing: true };
		searchSpy.mockResolvedValue(answer);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useConversationContentSearch("  granada ", true), { wrapper });

		await waitFor(() => expect(result.current).toEqual(answer));
		expect(searchSpy).toHaveBeenCalledTimes(1);
		expect(searchSpy).toHaveBeenCalledWith("granada", true, expect.objectContaining({ signal: expect.any(AbortSignal) }));
	});

	// The debounced value starts at the (blank) input, so the query is disabled from the first render on.
	it("does not search a blank query", () => {
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useConversationContentSearch("   ", false), { wrapper });

		expect(result.current).toBeUndefined();
		expect(searchSpy).not.toHaveBeenCalled();
	});

	it("re-asks while the node is still indexing and stops once it reports the backfill done", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		try {
			const partial = { conversations: [], indexing: true };
			const complete = { conversations: [{ conversationId: "c-1" }], indexing: false };
			searchSpy.mockResolvedValueOnce(partial).mockResolvedValue(complete);
			const { wrapper } = createProvidersWrapper();

			const { result } = renderHook(() => useConversationContentSearch("granada", false), { wrapper });
			await waitFor(() => expect(result.current).toEqual(partial));

			await vi.advanceTimersByTimeAsync(INDEXING_REFETCH_MS);
			await waitFor(() => expect(result.current).toEqual(complete));

			await vi.advanceTimersByTimeAsync(INDEXING_REFETCH_MS * 3);
			expect(searchSpy).toHaveBeenCalledTimes(2);
		} finally {
			vi.useRealTimers();
		}
	});

	it("withholds the previous query's answer while the new text is still debouncing", async () => {
		searchSpy.mockResolvedValue({ conversations: [], indexing: false });
		const { wrapper } = createProvidersWrapper();
		const { result, rerender } = renderHook(({ query }) => useConversationContentSearch(query, false), {
			wrapper,
			initialProps: { query: "gran" },
		});
		await waitFor(() => expect(result.current).toBeDefined());

		rerender({ query: "granada" });

		expect(result.current).toBeUndefined();
	});
});
