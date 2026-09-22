// @vitest-environment jsdom

// The embedding-model PRECONDITION, end to end through the real page. A node with no embedding model used to accept
// an upload and fail the document minutes later in the background embedder, with the only fix living on another page.
// What is pinned here is the wiring nothing smaller can hold: the same server-side embedding-model verdict both raises
// the alert and closes the dropzone, and the alert's button reaches the recommended-download endpoint. That verdict
// rides the document list because that endpoint resolves the model the ingestion lane will actually embed with.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
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
);

const embeddingMissing = en.pages.knowledgeBase.embeddingMissing;

/** The empty document list plus the server's embedding-model verdict, the upload gate's only input. */
function documentsRoute(embeddingModelAvailable: boolean) {
	return jsonRoute("get", "knowledge-base/documents", {
		items: [],
		embeddingModel: "nomic-embed-text",
		embeddingModelAvailable,
	});
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
		expect(screen.getByTestId("knowledge-embedding-open-node-settings").getAttribute("href")).toBe("/node-settings");

		const dropzone = screen.getByTestId("knowledge-upload-dropzone");
		expect(dropzone.getAttribute("aria-disabled")).toBe("true");
		expect(dropzone.getAttribute("tabindex")).toBe("-1");
		expect(screen.getByTestId<HTMLInputElement>("knowledge-upload-input").disabled).toBe(true);

		// A drop must be swallowed rather than queued: an upload request here would be an undeclared POST, which the
		// MSW lifecycle fails this test for by name.
		fireEvent.drop(dropzone, { dataTransfer: { files: [new File(["body"], "notes.md", { type: "text/markdown" })] } });
		expect(screen.queryByTestId("knowledge-upload-progress")).toBeNull();
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
