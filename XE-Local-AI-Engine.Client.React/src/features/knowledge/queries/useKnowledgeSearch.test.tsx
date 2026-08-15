// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";

import { useKnowledgeSearch } from "@/features/knowledge/queries/useKnowledgeSearch";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// The generation/collection guard this suite pins is a race between an in-flight POST and a re-render, so the mock has
// to be a request that is genuinely still open. MSW gives that without stubbing the generated mutation factory: the
// resolver parks on a promise the test resolves by hand, and the response still travels the real axios + zod path.

const searchPath = "knowledge-base/search";

/** One full search hit — every field the generated zod response validator requires. */
function hit(overrides: Record<string, unknown> = {}) {
	return {
		documentId: "11111111-1111-4111-8111-111111111111",
		chunkId: "22222222-2222-4222-8222-222222222222",
		title: "Runbook",
		section: null,
		content: "Restart the node.",
		source: "runbook.md",
		score: 0.9,
		chunkIndex: 0,
		documentStatus: "Indexed",
		servingLastKnownGood: false,
		collectionId: "PROJECT-A",
		sourcePath: null,
		contentKind: "text",
		language: null,
		symbol: null,
		pageNumber: null,
		startOffset: 0,
		endOffset: 17,
		...overrides,
	};
}

function deferred<T>() {
	let resolvePromise: (value: T) => void = () => undefined;
	const promise = new Promise<T>((resolve) => {
		resolvePromise = resolve;
	});
	return { promise, resolve: resolvePromise };
}

describe("useKnowledgeSearch over the real client", () => {
	it("posts the trimmed query with the active collection and surfaces the served hits", async () => {
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(searchPath), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json({ results: [hit()] });
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useKnowledgeSearch("PROJECT-A"), { wrapper });

		act(() => result.current.search("  restart  "));

		await waitFor(() => expect(result.current.hasSearched).toBe(true));
		expect(observedBody).toEqual({ query: "restart", limit: 10, collectionId: "PROJECT-A" });
		expect(result.current.results).toHaveLength(1);
		expect(result.current.lastQuery).toBe("restart");
	});

	it("discards a late response after the active collection changes", async () => {
		const gate = deferred<void>();
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(searchPath), async ({ request }) => {
				observedBody = await request.json();
				await gate.promise;
				return HttpResponse.json({ results: [hit()] });
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result, rerender } = renderHook(({ collectionId }) => useKnowledgeSearch(collectionId), {
			wrapper,
			initialProps: { collectionId: "PROJECT-A" },
		});

		act(() => result.current.search("old query"));
		await waitFor(() => expect(observedBody).toBeDefined());
		expect(observedBody).toEqual({ query: "old query", limit: 10, collectionId: "PROJECT-A" });

		rerender({ collectionId: "PROJECT-B" });
		await waitFor(() => expect(result.current.hasSearched).toBe(false));

		await act(async () => {
			gate.resolve();
			await gate.promise;
		});

		// The response arrives for a collection that is no longer active, so nothing from it may be committed.
		await waitFor(() => expect(result.current.results).toEqual([]));
		expect(result.current.hasSearched).toBe(false);
		expect(result.current.lastQuery).toBe("");
	});
});
