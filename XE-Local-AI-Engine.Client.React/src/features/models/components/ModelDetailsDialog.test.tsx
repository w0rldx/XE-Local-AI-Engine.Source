// @vitest-environment jsdom

// The dialog's Inspect tab now loads the shared panel lazily: it must still open, for a llama.cpp model only.

import { fireEvent, screen } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ModelDetailsDialog } from "@/features/models/components/ModelDetailsDialog";
import type { LocalModelViewModel } from "@/features/models/models/LocalModelModel";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({ CodeEditor: () => null }));

setupMswServer();

function model(provider: string): LocalModelViewModel {
	return {
		modelName: "org/Tiny-GGUF:Q4_K_M",
		provider,
		origin: null,
		sizeLabel: "",
		modifiedDateLabel: "",
		familyLabel: "",
		parameterSizeLabel: "",
		quantizationLabel: "",
		isSelected: false,
		kind: "Chat",
		detectedKind: "Chat",
		capabilities: [],
		isOverridden: false,
	};
}

function renderDialog(provider: string): void {
	renderWithProviders(
		<ModelDetailsDialog
			opened={true}
			onClose={vi.fn()}
			model={model(provider)}
			installedModels={[model(provider)]}
			details={undefined}
			detailsLoading={false}
			isActionPending={false}
			modelFitEnabled={false}
			onSetKind={vi.fn()}
			onResetKind={vi.fn()}
		/>,
	);
}

describe("ModelDetailsDialog inspect tab", () => {
	it("opens the lazily loaded panel for a llama.cpp model", async () => {
		server.use(
			http.get(localApiPath("models/:modelName/gguf-inspection"), () =>
				HttpResponse.json({
					modelName: "org/Tiny-GGUF:Q4_K_M",
					readFailure: null,
					ggufVersion: 3,
					architecture: "qwen35",
					displayName: "Tiny",
					parameterCount: null,
					fileType: "Q4_K_M",
					blockCount: 0,
					attentionHeadCount: null,
					attentionHeadCountKv: null,
					embeddingLength: null,
					contextLength: null,
					expertCount: null,
					chatTemplate: null,
					splitCount: null,
					tensorCount: 0,
					totalTensorBytes: null,
					headerBytes: 2048,
					fileSizeBytes: 4096,
					blocks: [],
					nonBlockTensors: [],
				}),
			),
		);
		renderDialog("llamacpp");

		fireEvent.click(await screen.findByRole("tab", { name: "Inspect" }));

		expect(await screen.findByTestId("model-inspect-panel")).toBeTruthy();
		expect(screen.getByText("qwen35")).toBeTruthy();
	});

	it("offers no Inspect tab for a model with no file to read", async () => {
		renderDialog("CodexOAuth");

		await screen.findAllByRole("tab");
		expect(screen.queryByRole("tab", { name: "Inspect" })).toBeNull();
	});
});
