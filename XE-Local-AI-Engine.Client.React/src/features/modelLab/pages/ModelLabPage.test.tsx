// @vitest-environment jsdom

// The Inspect tab against stubbed models and inspection routes (MSW): it opens the shared panel for a chosen installed
// model, offers that repo's other variants, and shows both ways a read can fail.

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Connected", stream: vi.fn() },
		whenStarted: Promise.resolve(),
		release: vi.fn(),
	}),
}));
vi.mock("@/features/modelLab/components/TraceExperiments", () => ({ TraceExperiments: () => null }));
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({ CodeEditor: () => null }));

setupMswServer();

const inspection = {
	modelName: "org/Tiny-GGUF:Q4_K_M",
	readFailure: null,
	ggufVersion: 3,
	architecture: "qwen35",
	displayName: "Tiny",
	parameterCount: 1000,
	fileType: "Q4_K_M",
	blockCount: 1,
	attentionHeadCount: 16,
	attentionHeadCountKv: 4,
	embeddingLength: 4096,
	contextLength: 262144,
	expertCount: null,
	chatTemplate: null,
	splitCount: null,
	tensorCount: 1,
	totalTensorBytes: 1024,
	headerBytes: 2048,
	fileSizeBytes: 4096,
	blocks: [
		{
			index: 0,
			byteSize: 1024,
			tensors: [{ name: "blk.0.attn_q.weight", shape: [256], ggmlType: "F32", ggmlTypeId: 0, offset: 0, byteSize: 1024 }],
		},
	],
	nonBlockTensors: [],
};

function stubRoutes(respond: () => Response): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				items: [
					["org/Tiny-GGUF:Q4_K_M", "llamacpp"],
					["org/Tiny-GGUF:Q3_K_XL", "llamacpp"],
					["gpt-6", "CodexOAuth"],
				].map(([modelName, provider]) => ({
					modelName,
					displayLabel: null,
					isSelected: false,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: false,
					isToolCapable: true,
					isOverridden: false,
					provider,
				})),
			}),
		),
		http.get(localApiPath("models/:modelName/gguf-inspection"), respond),
	);
}

async function openInspectDropdown(): Promise<ReturnType<typeof within>> {
	fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
	const input = screen.getByRole("combobox", { name: "Installed model" });
	fireEvent.click(input);
	return within(
		await waitFor(() => {
			const element = document.getElementById(input.getAttribute("aria-controls") ?? "");
			if (element === null) {
				throw new Error("the dropdown is not open yet");
			}
			return element;
		}),
	);
}

async function openInspectAndPick(): Promise<void> {
	const dropdown = await openInspectDropdown();
	fireEvent.click(await dropdown.findByRole("option", { name: "org/Tiny-GGUF:Q4_K_M", hidden: true }));
}

describe("ModelLabPage inspect tab", () => {
	it("offers installed llama.cpp models only", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		renderWithProviders(<ModelLabPage />);

		const dropdown = await openInspectDropdown();

		await dropdown.findByRole("option", { name: "org/Tiny-GGUF:Q3_K_XL", hidden: true });
		expect(dropdown.queryByRole("option", { name: "gpt-6", hidden: true })).toBeNull();
	});

	it.each([
		["model-lab-models-failed", () => HttpResponse.json({ title: "gone" }, { status: 500 })],
		["model-lab-no-models", () => HttpResponse.json({ items: [] })],
	])("shows %s on the tab when there is nothing to pick", async (testId, respond) => {
		server.use(http.get(localApiPath("models"), respond));
		renderWithProviders(<ModelLabPage />);

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));

		// The A/B panel stays mounted and shows the same notice, one per tab.
		expect(await screen.findAllByTestId(testId)).toHaveLength(2);
	});

	it("opens the shared panel for the chosen installed llama.cpp model", async () => {
		stubRoutes(() => HttpResponse.json(inspection));
		renderWithProviders(<ModelLabPage />);

		await openInspectAndPick();

		expect(await screen.findByTestId("model-inspect-panel")).toBeTruthy();
		expect(screen.getByText("qwen35")).toBeTruthy();
		expect(screen.getByTestId("model-inspect-block-strip")).toBeTruthy();
	});

	it("shows a partial read as the panel's warning", async () => {
		stubRoutes(() => HttpResponse.json({ ...inspection, readFailure: "truncated", blocks: [] }));
		renderWithProviders(<ModelLabPage />);

		await openInspectAndPick();

		expect(await screen.findByText(/could not be read completely \(truncated\)/)).toBeTruthy();
	});

	it("shows a failed request as an error and no panel", async () => {
		stubRoutes(() => HttpResponse.json({ title: "gone" }, { status: 500 }));
		renderWithProviders(<ModelLabPage />);

		await openInspectAndPick();

		await screen.findByRole("alert");
		expect(screen.queryByTestId("model-inspect-panel")).toBeNull();
	});
});
