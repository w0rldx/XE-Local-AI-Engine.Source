// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

const { searchMutation } = vi.hoisted(() => ({ searchMutation: vi.fn() }));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	searchKnowledgeMutation: () => ({ mutationFn: searchMutation }),
}));

import { useKnowledgeSearch } from "@/features/knowledge/queries/useKnowledgeSearch";

function deferred<T>() {
	let resolvePromise: (value: T) => void = () => undefined;
	const promise = new Promise<T>((resolve) => {
		resolvePromise = resolve;
	});
	return { promise, resolve: resolvePromise };
}

function makeWrapper() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	};
}

describe("useKnowledgeSearch", () => {
	it("discards a late response after the active collection changes", async () => {
		const oldCollectionResponse = deferred<{ results: Array<{ chunkId: string; collectionId: string }> }>();
		searchMutation.mockReturnValueOnce(oldCollectionResponse.promise);
		const Wrapper = makeWrapper();
		const { result, rerender } = renderHook(({ collectionId }) => useKnowledgeSearch(collectionId), {
			wrapper: Wrapper,
			initialProps: { collectionId: "PROJECT-A" },
		});

		act(() => result.current.search("old query"));
		await waitFor(() => expect(searchMutation).toHaveBeenCalledOnce());
		expect(searchMutation.mock.calls[0]?.[0]).toEqual(
			expect.objectContaining({ body: expect.objectContaining({ collectionId: "PROJECT-A", query: "old query" }) }),
		);

		rerender({ collectionId: "PROJECT-B" });
		await waitFor(() => expect(result.current.hasSearched).toBe(false));

		await act(async () => {
			oldCollectionResponse.resolve({ results: [{ chunkId: "old-chunk", collectionId: "PROJECT-A" }] });
			await oldCollectionResponse.promise;
		});

		expect(result.current.results).toEqual([]);
		expect(result.current.hasSearched).toBe(false);
		expect(result.current.lastQuery).toBe("");
	});
});
