// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KnowledgeSearchPanel } from "@/features/knowledge/components/KnowledgeSearchPanel";
import type { KnowledgeDocument, KnowledgeSearchHit } from "@/features/knowledge/models/KnowledgeModels";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function knowledgeDocument(overrides: Partial<KnowledgeDocument> = {}): KnowledgeDocument {
	return {
		documentId: "doc-1",
		displayName: "12-security-and-privacy.md",
		status: "Indexed",
		chunkCount: 3,
		embeddingModel: "nomic-embed-text",
		staleModel: false,
		sizeBytes: 1024,
		createdAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

function hit(overrides: Partial<KnowledgeSearchHit> = {}): KnowledgeSearchHit {
	return {
		documentId: "doc-1",
		chunkId: "chunk-1",
		title: "A title",
		content: "some content",
		source: "knowledge-base",
		score: 0.5,
		chunkIndex: 0,
		documentStatus: "Indexed",
		servingLastKnownGood: false,
		...overrides,
	};
}

function searchBag(results: readonly KnowledgeSearchHit[]): UseKnowledgeSearchResult {
	return {
		results,
		isSearching: false,
		error: undefined,
		hasSearched: true,
		lastQuery: "q",
		search: vi.fn(),
		reset: vi.fn(),
	};
}

function stubMatchMedia(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
}

describe("KnowledgeSearchPanel disclosure badge", () => {
	beforeEach(() => {
		stubMatchMedia();
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the last-known-good badge only on stale hits", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[]}
				search={searchBag([
					hit({ chunkId: "fresh", documentStatus: "Indexed", servingLastKnownGood: false }),
					hit({ chunkId: "stale", documentStatus: "Pending", servingLastKnownGood: true }),
				])}
			/>,
		);

		// Exactly one of the two rendered hits (the pending re-index) discloses last-known-good.
		expect(screen.getAllByTestId("knowledge-last-known-good")).toHaveLength(1);
	});

	it("shows no badge when every hit is freshly indexed", () => {
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit({ servingLastKnownGood: false })])} />);

		expect(screen.queryByTestId("knowledge-last-known-good")).toBeNull();
	});
});

describe("KnowledgeSearchPanel hit title", () => {
	beforeEach(() => {
		stubMatchMedia();
	});

	afterEach(() => {
		cleanup();
	});

	it("labels a hit with the document's display name instead of the GUID storage path", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[knowledgeDocument()]}
				search={searchBag([hit({ documentId: "doc-1", title: "48ecbdcd-2b4c-4fa4-b748-d368a2510168.md" })])}
			/>,
		);

		expect(screen.getByTestId("knowledge-search-hit-title").textContent).toBe("12-security-and-privacy.md");
	});

	it("falls back to the server title when the document is not in the loaded list", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[knowledgeDocument({ documentId: "other-doc" })]}
				search={searchBag([hit({ documentId: "doc-1", title: "48ecbdcd-2b4c-4fa4-b748-d368a2510168.md" })])}
			/>,
		);

		expect(screen.getByTestId("knowledge-search-hit-title").textContent).toBe("48ecbdcd-2b4c-4fa4-b748-d368a2510168.md");
	});

	it("keeps the heading path as the section sub-label beneath the resolved name", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[knowledgeDocument()]}
				search={searchBag([hit({ documentId: "doc-1", section: "Encryption at rest" })])}
			/>,
		);

		expect(screen.getByTestId("knowledge-search-hit-title").textContent).toBe("12-security-and-privacy.md");
		expect(screen.queryByText("Encryption at rest")).not.toBeNull();
	});

	it("labels every hit from the same document identically", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[knowledgeDocument()]}
				search={searchBag([
					hit({ chunkId: "chunk-1", documentId: "doc-1", section: "Encryption at rest" }),
					hit({ chunkId: "chunk-2", documentId: "doc-1", section: "Key rotation" }),
				])}
			/>,
		);

		const titles = screen.getAllByTestId("knowledge-search-hit-title");
		expect(titles).toHaveLength(2);
		expect(titles.map((title) => title.textContent)).toEqual(["12-security-and-privacy.md", "12-security-and-privacy.md"]);
	});
});
