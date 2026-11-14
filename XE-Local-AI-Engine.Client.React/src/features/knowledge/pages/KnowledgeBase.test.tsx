// @vitest-environment jsdom

// The embedding-model PRECONDITION, end to end through the real page. A node with no embedding model used to accept
// an upload and fail the document minutes later in the background embedder, with the only fix living on another page.
// What is pinned here is the wiring nothing smaller can hold: the same server-side embedding-model verdict both raises
// the alert and closes the dropzone, and the alert's button reaches the recommended-download endpoint. That verdict
// rides the document list because that endpoint resolves the model the ingestion lane will actually embed with.

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// The knowledge hub and the GGUF acquisition hub both open on mount; sockets are their own files' subject.
vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn(async () => undefined) },
		whenStarted: Promise.resolve(),
		onReconnected: () => vi.fn(),
		onReconnecting: () => vi.fn(),
		onClosed: () => vi.fn(),
		release: vi.fn(),
	}),
}));

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { KnowledgeBase } from "@/features/knowledge/pages/KnowledgeBase";
import en from "@/locales/en.json";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Ambient to every case: the page's own reads, none of which this file is about.
setupMswServer(
	documentsRoute(true),
	jsonRoute("get", "development/repositories", { items: [] }),
	jsonRoute("get", "model-fit/gguf/downloads", { items: [] }),
	jsonRoute("get", "model-fit/gguf/imports", { items: [] }),
	jsonRoute("get", "knowledge-base/collections", { items: [{ collectionId: "DEFAULT", documentCount: 0 }] }),
);

const embeddingMissing = en.pages.knowledgeBase.embeddingMissing;

/** The empty document list plus the server's embedding-model verdict, the upload gate's only input. */
function documentsRoute(embeddingModelAvailable: boolean) {
	return jsonRoute("get", "knowledge-base/documents", {
		items: [],
		totalCount: 0,
		hasStaleDocuments: false,
		hasSearchableDocuments: false,
		embeddingModel: "nomic-embed-text",
		embeddingModelAvailable,
	});
}

/** The response schema validates `documentId` as a GUID. */
function documentId(index: number): string {
	return `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`;
}

function documentRow(index: number) {
	return {
		documentId: documentId(index),
		displayName: `file-${index}.md`,
		status: "Indexed",
		chunkCount: 1,
		embeddingModel: "nomic-embed-text",
		staleModel: false,
		sizeBytes: 10,
		createdAtUtc: 1_700_000_000_000 - index,
		collectionId: "DEFAULT",
		sourceKind: "upload",
	};
}

function embeddingAvailable(available: boolean): void {
	server.use(documentsRoute(available));
}

function renderPage() {
	return renderWithProviders(
		<ConfirmProvider>
			<KnowledgeBase />
		</ConfirmProvider>,
		{ withRouter: true },
	);
}

afterEach(cleanup);

describe("KnowledgeBase embedding-model precondition", () => {
	it("warns and closes the dropzone while the server resolves no embedding model", async () => {
		embeddingAvailable(false);

		renderPage();

		const alert = await screen.findByTestId("knowledge-embedding-missing-alert");
		expect(alert.textContent).toContain(embeddingMissing.title);
		expect(alert.textContent).toContain(embeddingMissing.body);
		expect(screen.getByTestId("knowledge-embedding-open-node-settings").getAttribute("href")).toBe(
			"/node-settings?section=knowledge",
		);

		const dropzone = screen.getByTestId("knowledge-upload-dropzone");
		expect(dropzone.getAttribute("aria-disabled")).toBe("true");
		expect(dropzone.getAttribute("tabindex")).toBe("-1");
		expect(screen.getByTestId<HTMLInputElement>("knowledge-upload-input").disabled).toBe(true);

		// A drop must be swallowed rather than queued: an upload request here would be an undeclared POST, which the
		// MSW lifecycle fails this test for by name.
		fireEvent.drop(dropzone, { dataTransfer: { files: [new File(["body"], "notes.md", { type: "text/markdown" })] } });
		expect(screen.queryByTestId("knowledge-upload-progress")).toBeNull();
	});

	it("puts the warning before the page header, so a phone's first screen shows it", async () => {
		embeddingAvailable(false);

		renderPage();

		const alert = await screen.findByTestId("knowledge-embedding-missing-alert");
		const heading = screen.getByRole("heading", { name: en.pages.knowledgeBase.title });
		expect(alert.compareDocumentPosition(heading) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
	});

	it("shows no warning and leaves the dropzone open once the server resolves an embedding model", async () => {
		embeddingAvailable(true);

		renderPage();

		const dropzone = await screen.findByTestId("knowledge-upload-dropzone");
		await waitFor(() => expect(dropzone.getAttribute("tabindex")).toBe("0"));
		expect(screen.queryByTestId("knowledge-embedding-missing-alert")).toBeNull();
		expect(dropzone.getAttribute("aria-disabled")).toBe("false");
		expect(screen.getByTestId<HTMLInputElement>("knowledge-upload-input").disabled).toBe(false);
	});

	it("starts the recommended embedding download from the alert", async () => {
		embeddingAvailable(false);
		const requested = vi.fn();
		server.use(
			http.post(localApiPath("knowledge-base/embedding/download-recommended"), () => {
				requested();
				return HttpResponse.json({
					modelName: "nomic-embed-text-v1.5",
					repoId: "nomic-ai/nomic-embed-text-v1.5-GGUF",
					quant: "Q4_K_M",
					alreadyInstalled: false,
					alreadyInFlight: false,
				});
			}),
		);

		renderPage();

		fireEvent.click(await screen.findByTestId("knowledge-embedding-download-recommended"));

		await waitFor(() => expect(requested).toHaveBeenCalledTimes(1));
	});
});

// A repository import can hold tens of thousands of documents: the list is paged by the server, and the availability
// poll must not ship the list at all.
describe("KnowledgeBase document paging", () => {
	it("asks for one bounded page, reaches the next through the pager, and polls availability with one row", async () => {
		const requested: URLSearchParams[] = [];
		server.use(
			http.get(localApiPath("knowledge-base/documents"), ({ request }) => {
				const query = new URL(request.url).searchParams;
				requested.push(query);
				const limit = Number(query.get("limit"));
				const offset = Number(query.get("offset") ?? "0");
				return HttpResponse.json({
					items: Array.from({ length: Math.min(limit, 30 - offset) }, (_, index) => documentRow(offset + index)),
					totalCount: 30,
					hasStaleDocuments: true,
					hasSearchableDocuments: true,
					embeddingModel: "nomic-embed-text",
					embeddingModelAvailable: true,
				});
			}),
		);

		renderPage();

		expect(await screen.findByTestId(`knowledge-row-${documentId(0)}`)).toBeTruthy();
		expect(screen.queryByTestId(`knowledge-row-${documentId(25)}`)).toBeNull();
		// The whole-collection stale flag offers the reindex even though no row on this page is stale.
		expect(screen.getByTestId("knowledge-reindex-stale")).toBeTruthy();
		expect(requested.some((query) => query.get("limit") === "25" && query.get("offset") === "0")).toBe(true);
		expect(requested.some((query) => query.get("limit") === "1" && !query.has("offset"))).toBe(true);
		expect(requested.every((query) => Number(query.get("limit")) <= 200)).toBe(true);

		const controls = screen.getByTestId("knowledge-pagination-controls");
		fireEvent.click(within(controls).getByRole("button", { name: "2" }));

		expect(await screen.findByTestId(`knowledge-row-${documentId(25)}`)).toBeTruthy();
		expect(requested.some((query) => query.get("offset") === "25")).toBe(true);
		expect(screen.getByTestId("knowledge-pagination-range").textContent).toContain("30");
	});
});

describe("KnowledgeBase known collections", () => {
	it("offers the node's other collections and opens one on click", async () => {
		server.use(
			jsonRoute("get", "knowledge-base/collections", {
				items: [
					{ collectionId: "DEFAULT", documentCount: 2 },
					{ collectionId: "RESEARCH", documentCount: 1 },
				],
			}),
		);

		renderPage();

		const known = await screen.findByRole("group", { name: en.pages.knowledgeBase.collection.known });
		// The open collection is the badge, so only the others are offered.
		expect(within(known).queryByRole("button", { name: "DEFAULT" })).toBeNull();
		fireEvent.click(within(known).getByRole("button", { name: "RESEARCH" }));

		await waitFor(() => expect(screen.getByTestId("knowledge-active-collection").textContent).toBe("RESEARCH"));
		expect(screen.getByTestId<HTMLInputElement>("knowledge-collection-input").value).toBe("RESEARCH");
		expect(await within(known).findByRole("button", { name: "DEFAULT" })).toBeDefined();
	});
});
