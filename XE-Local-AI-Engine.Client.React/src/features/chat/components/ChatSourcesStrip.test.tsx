// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ChatMessageSource } from "@/features/chat/models/ChatModels";
import type { KnowledgeDocumentDetail } from "@/features/knowledge/models/KnowledgeModels";

// Control the document-detail read so a card click can be asserted without a real network fetch. The mock records the
// (documentId, enabled) args so the test can prove the clicked source's document id is what drives the drawer query.
const { detailMock } = vi.hoisted(() => ({ detailMock: vi.fn() }));

vi.mock("@/features/knowledge/queries/useKnowledgeDocuments", () => ({
	useKnowledgeDocumentDetail: (documentId: string, enabled: boolean) => detailMock(documentId, enabled),
}));

import { ChatSourcesStrip } from "@/features/chat/components/ChatSourcesStrip";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{ui}</MantineProvider>
		</QueryClientProvider>,
	);
}

function source(overrides: Partial<ChatMessageSource> = {}): ChatMessageSource {
	return {
		documentId: "11111111-1111-1111-1111-111111111111",
		chunkId: "22222222-2222-2222-2222-222222222222",
		title: "Design Doc",
		section: "Overview",
		score: 0.87,
		...overrides,
	};
}

function detail(overrides: Partial<KnowledgeDocumentDetail> = {}): KnowledgeDocumentDetail {
	return {
		documentId: "11111111-1111-1111-1111-111111111111",
		displayName: "Design Doc.md",
		status: "Indexed",
		chunkCount: 1,
		embeddingModel: "nomic-embed-text",
		staleModel: false,
		sizeBytes: 2048,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_000_000,
		collectionId: "default",
		sourceKind: "upload",
		chunks: [
			{
				chunkIndex: 0,
				headingPath: "Overview",
				content: "Intro body",
				startOffset: 0,
				endOffset: 10,
				contentKind: "prose",
			},
		],
		...overrides,
	};
}

describe("ChatSourcesStrip", () => {
	beforeEach(() => {
		detailMock.mockReset();
		detailMock.mockReturnValue({ data: undefined, isFetching: false });
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
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
		Element.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a card per source with title and section", () => {
		renderWithProviders(
			<ChatSourcesStrip
				sources={[source({ title: "Alpha", section: "Intro" }), source({ chunkId: "33", title: "Beta", section: undefined })]}
			/>,
		);

		expect(screen.getByTestId("chat-sources-strip")).toBeTruthy();
		expect(screen.getAllByTestId("chat-source-card")).toHaveLength(2);
		expect(screen.getByText("Alpha")).toBeTruthy();
		expect(screen.getByText("Intro")).toBeTruthy();
		expect(screen.getByText("Beta")).toBeTruthy();
	});

	it("starts collapsed and toggles open on click", () => {
		renderWithProviders(<ChatSourcesStrip sources={[source()]} />);

		const toggle = screen.getByTestId("chat-sources-toggle");
		expect(toggle.getAttribute("aria-expanded")).toBe("false");

		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
	});

	it("renders nothing when there are no sources", () => {
		renderWithProviders(<ChatSourcesStrip sources={[]} />);

		expect(screen.queryByTestId("chat-sources-strip")).toBeNull();
	});

	it("opens the document drawer for the clicked source's documentId", async () => {
		detailMock.mockReturnValue({ data: detail(), isFetching: false });
		renderWithProviders(
			<ChatSourcesStrip
				sources={[source({ documentId: "doc-a", title: "Alpha" }), source({ documentId: "doc-b", chunkId: "b1", title: "Beta" })]}
			/>,
		);

		// Before any click the detail query is disabled (drawer closed).
		expect(detailMock).toHaveBeenLastCalledWith(expect.any(String), false);

		fireEvent.click(screen.getByText("Beta"));

		// The clicked source's document id now drives the (enabled) detail query.
		expect(detailMock).toHaveBeenLastCalledWith("doc-b", true);
		// The shared knowledge drawer is open, showing the loaded document title (rendered after the modal transition).
		expect(await screen.findByText("Design Doc.md")).toBeTruthy();
	});
});
