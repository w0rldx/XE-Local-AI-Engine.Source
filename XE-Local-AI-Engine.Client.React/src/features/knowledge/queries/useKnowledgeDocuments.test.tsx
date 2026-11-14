// @vitest-environment jsdom

// The document list is paged by the server, so the next page is a query with no data of its own yet. Holding the
// previous page keeps the pager's total while it loads; holding ANOTHER collection's rows would show collection A
// under collection B's name until B's fetch lands.

import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { useKnowledgeDocumentPage } from "@/features/knowledge/queries/useKnowledgeDocuments";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

function documentRow(collectionId: string, index: number) {
	return {
		documentId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
		displayName: `${collectionId}-${index}`,
		status: "Indexed",
		chunkCount: 1,
		embeddingModel: "nomic-embed-text",
		staleModel: false,
		sizeBytes: 10,
		createdAtUtc: 1,
		collectionId,
		sourceKind: "upload",
	};
}

describe("useKnowledgeDocumentPage", () => {
	it("holds the previous page of the same collection while the next loads, but never another collection's rows", async () => {
		// Only collection A's first page answers; every other request waits until the test is done looking at it.
		let release = (): void => undefined;
		const held = new Promise<void>((resolve) => {
			release = resolve;
		});
		server.use(
			http.get(localApiPath("knowledge-base/documents"), async ({ request }) => {
				const query = new URL(request.url).searchParams;
				const collectionId = query.get("collectionId") ?? "";
				const offset = Number(query.get("offset"));
				if (collectionId !== "A" || offset !== 0) {
					await held;
				}
				return HttpResponse.json({
					items: [documentRow(collectionId, offset)],
					totalCount: 60,
					hasStaleDocuments: false,
					hasSearchableDocuments: true,
					embeddingModel: "nomic-embed-text",
					embeddingModelAvailable: true,
				});
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result, rerender } = renderHook(
			({ collectionId, offset }: { collectionId: string; offset: number }) => useKnowledgeDocumentPage(collectionId, 25, offset),
			{ wrapper, initialProps: { collectionId: "A", offset: 0 } },
		);
		await waitFor(() => expect(result.current.data?.items[0]?.displayName).toBe("A-0"));

		rerender({ collectionId: "A", offset: 25 });
		expect(result.current.isPlaceholderData).toBe(true);
		expect(result.current.data?.totalCount).toBe(60);

		rerender({ collectionId: "B", offset: 0 });
		expect(result.current.data).toBeUndefined();

		release();
		await waitFor(() => expect(result.current.data?.items[0]?.displayName).toBe("B-0"));
	});
});
