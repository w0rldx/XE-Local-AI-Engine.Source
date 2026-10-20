// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KnowledgeSearchPanel } from "@/features/knowledge/components/KnowledgeSearchPanel";
import type { KnowledgeDocument, KnowledgeSearchHit } from "@/features/knowledge/models/KnowledgeModels";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
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
		collectionId: "DEFAULT",
		sourceKind: "upload",
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
		scoreKind: "Fusion",
		chunkIndex: 0,
		documentStatus: "Indexed",
		servingLastKnownGood: false,
		collectionId: "DEFAULT",
		contentKind: "prose",
		startOffset: 0,
		endOffset: 12,
		...overrides,
	};
}

function searchBag(
	results: readonly KnowledgeSearchHit[],
	degradations: UseKnowledgeSearchResult["degradations"] = [],
	weakMatch: boolean | null = null,
): UseKnowledgeSearchResult {
	return {
		results,
		degradations,
		weakMatch,
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

describe("KnowledgeSearchPanel score kind", () => {
	beforeEach(() => {
		stubMatchMedia();
	});

	afterEach(() => {
		cleanup();
	});

	// Formatting and localisation live in KnowledgeScoreBadge.test.tsx; this only proves each hit renders its kind.
	it("renders a score-kind badge per hit instead of a bare number", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
				documents={[]}
				search={searchBag([
					hit({ chunkId: "reranked", score: 7.3312, scoreKind: "Rerank" }),
					hit({ chunkId: "fused", score: 0.03125, scoreKind: "Fusion" }),
				])}
			/>,
		);

		expect(screen.getAllByTestId("knowledge-score-kind").map((badge) => badge.textContent)).toEqual([
			"Reranked Cross-encoder relevance 7.33",
			"Hybrid match Fusion score 0.031",
		]);
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

describe("KnowledgeSearchPanel degradation notice", () => {
	beforeEach(() => {
		stubMatchMedia();
	});

	afterEach(() => {
		cleanup();
	});

	it("names a cold reranker instead of serving fusion order silently", () => {
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit()], ["RerankerUnavailable"])} />);

		expect(screen.getByTestId("knowledge-search-degradation-RerankerUnavailable").textContent).toContain(
			"The reranker is not ready yet",
		);
		expect(screen.queryByTestId("knowledge-search-degradation-LexicalOnly")).toBeNull();
	});

	it("names an embedder change instead of serving stale vectors silently", () => {
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit()], ["EmbedderChanged"])} />);

		expect(screen.getByTestId("knowledge-search-degradation-EmbedderChanged").textContent).toContain(
			"The embedding model changed since some documents were indexed",
		);
	});

	it("skips a degradation value it has no notice for instead of throwing", () => {
		const unknown = "SomethingNewer" as UseKnowledgeSearchResult["degradations"][number];
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit()], [unknown, "LexicalOnly"])} />);

		expect(screen.queryByTestId("knowledge-search-degradation-SomethingNewer")).toBeNull();
		expect(screen.getByTestId("knowledge-search-degradation-LexicalOnly")).toBeTruthy();
	});

	it("shows no notice when the full pipeline ran", () => {
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit()])} />);

		expect(screen.queryByTestId(/knowledge-search-degradation-/)).toBeNull();
	});
});

describe("KnowledgeSearchPanel weak-match notice", () => {
	beforeEach(() => {
		stubMatchMedia();
	});

	afterEach(() => {
		cleanup();
	});

	it("says the documents hold no strong match when the reranked best hit scored low, and keeps the hits", () => {
		renderWithProviders(
			<KnowledgeSearchPanel documents={[]} search={searchBag([hit({ scoreKind: "Rerank", score: -2.1 })], [], true)} />,
		);

		expect(screen.getByTestId("knowledge-search-weak-match").textContent).toContain("No strong match in your documents");
		expect(screen.getByTestId("knowledge-search-results")).toBeTruthy();
	});

	it.each([false, null])("shows no weak-match notice when the verdict is %s", (weakMatch) => {
		renderWithProviders(<KnowledgeSearchPanel documents={[]} search={searchBag([hit()], [], weakMatch)} />);

		expect(screen.queryByTestId("knowledge-search-weak-match")).toBeNull();
	});
});
