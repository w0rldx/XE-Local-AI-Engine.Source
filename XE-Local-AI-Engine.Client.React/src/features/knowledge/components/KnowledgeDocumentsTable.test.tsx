// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { KnowledgeDocumentsTable } from "@/features/knowledge/components/KnowledgeDocumentsTable";
import type { KnowledgeDocument } from "@/features/knowledge/models/KnowledgeModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function knowledgeDocument(overrides: Partial<KnowledgeDocument> = {}): KnowledgeDocument {
	return {
		documentId: "doc-1",
		displayName: "manual.pdf",
		status: "Indexed",
		chunkCount: 3,
		embeddingModel: "nomic-embed-text",
		staleModel: false,
		sizeBytes: 1024,
		createdAtUtc: 1_700_000_000_000,
		collectionId: "DEFAULT",
		sourceKind: "upload",
		...overrides,
	};
}

function renderTable(documents: readonly KnowledgeDocument[]) {
	return renderWithProviders(
		<KnowledgeDocumentsTable
			documents={documents}
			isActionPending={false}
			onOpenDetail={vi.fn()}
			onReindex={vi.fn()}
			onDelete={vi.fn()}
		/>,
	);
}

describe("KnowledgeDocumentsTable", () => {
	afterEach(() => {
		cleanup();
	});

	it("prints an upload's source kind once when it has no source path", () => {
		renderTable([knowledgeDocument()]);

		expect(screen.getByTestId("knowledge-source-doc-1").textContent).toBe("upload");
	});

	it("shows a repository file's path above its source kind", () => {
		renderTable([knowledgeDocument({ sourcePath: "src/app.ts", sourceKind: "repository" })]);

		expect(screen.getByTestId("knowledge-source-doc-1").textContent).toBe("src/app.tsrepository");
	});

	it("shows no embedding model or stale badge for a document with no embedded chunks", () => {
		renderTable([knowledgeDocument({ status: "Failed", chunkCount: 0, staleModel: true })]);

		expect(screen.getByTestId("knowledge-embedding-model-doc-1").textContent).toBe("—");
		expect(screen.queryByText("Stale")).toBeNull();
	});

	it("shows the embedding model of an indexed document", () => {
		renderTable([knowledgeDocument()]);

		expect(screen.getByTestId("knowledge-embedding-model-doc-1").textContent).toBe("nomic-embed-text");
	});

	it("shows a coded failure reason translated instead of the stored text", () => {
		renderTable([
			knowledgeDocument({
				status: "Failed",
				chunkCount: 0,
				failureReason: "Extraction failed (FileFormatException).",
				failureReasonCode: "unreadable-file",
			}),
		]);

		expect(screen.getByTestId("knowledge-failure-doc-1").textContent).toBe(
			"The file could not be read. It may be damaged or not a valid file of its type.",
		);
	});

	it("falls back to the stored text for a reason with no code", () => {
		renderTable([knowledgeDocument({ status: "Failed", chunkCount: 0, failureReason: "Document text exceeds the cap." })]);

		expect(screen.getByTestId("knowledge-failure-doc-1").textContent).toBe("Document text exceeds the cap.");
	});

	it("floors the status badge at its full label width so a squeezed column cannot clip it", () => {
		renderTable([knowledgeDocument()]);

		expect(screen.getByTestId("knowledge-status-box-Indexed").style.minWidth).toBe("max-content");
	});
});
