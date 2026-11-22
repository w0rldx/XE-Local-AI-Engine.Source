// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { KnowledgeDocumentDrawer } from "@/features/knowledge/components/KnowledgeDocumentDrawer";
import type { KnowledgeDocumentDetail } from "@/features/knowledge/models/KnowledgeModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const waitingText = "Waiting for the embedding model, which was unloaded. Ingestion continues once it is loaded again.";

function documentDetail(overrides: Partial<KnowledgeDocumentDetail> = {}): KnowledgeDocumentDetail {
	return {
		documentId: "doc-1",
		displayName: "manual.pdf",
		status: "Pending",
		chunkCount: 0,
		embeddingModel: "",
		staleModel: false,
		sizeBytes: 1024,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_001_000,
		collectionId: "DEFAULT",
		sourceKind: "upload",
		chunks: [],
		...overrides,
	};
}

function renderDrawer(detail: KnowledgeDocumentDetail) {
	return renderWithProviders(<KnowledgeDocumentDrawer opened={true} detail={detail} isLoading={false} onClose={vi.fn()} />);
}

describe("KnowledgeDocumentDrawer", () => {
	afterEach(() => {
		cleanup();
	});

	it("says why a Pending document parked for an unloaded embedding model waits", () => {
		renderDrawer(
			documentDetail({ failureReason: "Waiting for the embedding model.", failureReasonCode: "embedding-model-ejected" }),
		);

		expect(screen.getByTestId("knowledge-detail-waiting").textContent).toBe(waitingText);
	});

	it("shows no waiting hint for a Pending document without a reason", () => {
		renderDrawer(documentDetail());

		expect(screen.queryByTestId("knowledge-detail-waiting")).toBeNull();
	});

	// Only embedded chunks carry a model: a Pending row's field holds the configured default, not what embedded it.
	it("shows the embedding model only once the document has chunks", () => {
		renderDrawer(documentDetail({ embeddingModel: "configured-default", staleModel: true }));

		expect(screen.getByTestId("knowledge-detail-embedding-model").textContent).toBe("—");
		expect(screen.queryByText("Stale")).toBeNull();
		cleanup();

		renderDrawer(documentDetail({ status: "Indexed", chunkCount: 3, embeddingModel: "nomic-embed-text" }));

		expect(screen.getByTestId("knowledge-detail-embedding-model").textContent).toBe("nomic-embed-text");
	});

	it("keeps a Failed document's translated reason and shows no waiting hint", () => {
		renderDrawer(
			documentDetail({
				status: "Failed",
				failureReason: "Extraction failed (FileFormatException).",
				failureReasonCode: "unreadable-file",
			}),
		);

		expect(screen.getByText("The file could not be read. It may be damaged or not a valid file of its type.")).toBeTruthy();
		expect(screen.queryByTestId("knowledge-detail-waiting")).toBeNull();
	});
});
