// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KnowledgeSearchPanel } from "@/features/knowledge/components/KnowledgeSearchPanel";
import type { KnowledgeSearchHit } from "@/features/knowledge/models/KnowledgeModels";
import type { UseKnowledgeSearchResult } from "@/features/knowledge/queries/useKnowledgeSearch";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
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

describe("KnowledgeSearchPanel disclosure badge", () => {
	beforeEach(() => {
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
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the last-known-good badge only on stale hits", () => {
		renderWithProviders(
			<KnowledgeSearchPanel
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
		renderWithProviders(<KnowledgeSearchPanel search={searchBag([hit({ servingLastKnownGood: false })])} />);

		expect(screen.queryByTestId("knowledge-last-known-good")).toBeNull();
	});
});
