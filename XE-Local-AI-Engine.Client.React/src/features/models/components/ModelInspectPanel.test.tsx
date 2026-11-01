// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { inspectionSpy } = vi.hoisted(() => ({ inspectionSpy: vi.fn() }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	getModelGgufInspectionOptions: (options: { path: { modelName: string } }) => ({
		queryKey: ["getModelGgufInspection", options.path.modelName],
		queryFn: () => inspectionSpy(options.path.modelName),
	}),
}));
// Monaco is lazy and heavy; the panel only needs to hand it the template text.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<pre data-testid={testId}>{value}</pre>
	),
}));

import { ModelInspectPanel } from "@/features/models/components/ModelInspectPanel";
import { testMantineTheme } from "@/test/MantineTestRender";

function tensor(name: string, shape: number[], ggmlType: string | null, byteSize: number | null) {
	return { name, shape, ggmlType, ggmlTypeId: ggmlType === null ? 99 : 12, offset: 0, byteSize };
}

const inspection = {
	modelName: "org/Tiny-GGUF:Q4_K_M",
	readFailure: null,
	ggufVersion: 3,
	architecture: "qwen35",
	displayName: "Tiny",
	parameterCount: 26_895_998_464,
	fileType: "Q4_K_M",
	blockCount: 2,
	attentionHeadCount: 16,
	attentionHeadCountKv: 4,
	embeddingLength: 4096,
	contextLength: 262144,
	expertCount: null,
	chatTemplate: "{{ messages }}",
	splitCount: null,
	tensorCount: 4,
	totalTensorBytes: null,
	headerBytes: 2048,
	fileSizeBytes: 4096,
	blocks: [
		{ index: 0, byteSize: 1024, tensors: [tensor("blk.0.attn_norm.weight", [256], "F32", 1024)] },
		{ index: 1, byteSize: null, tensors: [tensor("blk.1.future.weight", [256, 4], null, null)] },
	],
	nonBlockTensors: [tensor("token_embd.weight", [4096, 248320], "Q4_K", 572_129_280)],
};

function renderPanel() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ModelInspectPanel modelName="org/Tiny-GGUF:Q4_K_M" />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("ModelInspectPanel", () => {
	beforeEach(() => {
		inspectionSpy.mockReset();
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

	it("renders the metadata card, the chat template and one group per block", async () => {
		inspectionSpy.mockResolvedValue(inspection);

		renderPanel();

		expect(await screen.findByTestId("model-inspect-panel")).toBeTruthy();
		expect(inspectionSpy).toHaveBeenCalledWith("org/Tiny-GGUF:Q4_K_M");
		expect(screen.getByText("qwen35")).toBeTruthy();
		expect(screen.getByText("Q4_K_M")).toBeTruthy();
		expect(screen.getByText("16 (4)")).toBeTruthy();
		expect(screen.getByText(`26.9B (${(26_895_998_464).toLocaleString()})`)).toBeTruthy();

		// Every group starts collapsed; opening one mounts its content.
		fireEvent.click(screen.getByText("Chat template"));
		expect((await screen.findByTestId("model-inspect-template")).textContent).toBe("{{ messages }}");
		fireEvent.click(screen.getByText("Outside blocks"));
		expect(await screen.findByText("token_embd.weight")).toBeTruthy();

		const block0 = screen.getByTestId("model-inspect-block-0");
		expect(within(block0).getByText("1 tensors · 1 KB")).toBeTruthy();
		fireEvent.click(within(block0).getByText("Block 0"));
		expect(await within(block0).findByText("attn_norm.weight")).toBeTruthy();

		const block1 = screen.getByTestId("model-inspect-block-1");
		expect(within(block1).getByText("1 tensors · Unknown")).toBeTruthy();
		fireEvent.click(within(block1).getByText("Block 1"));
		expect(await within(block1).findByText("type 99")).toBeTruthy();
	});

	it("warns when the tensor block could not be read completely", async () => {
		inspectionSpy.mockResolvedValue({
			...inspection,
			readFailure: "truncated",
			parameterCount: null,
			blocks: [],
			nonBlockTensors: [],
		});

		renderPanel();

		expect(
			await screen.findByText("The file could not be read completely (truncated). Only the header metadata is shown."),
		).toBeTruthy();
		expect(screen.getByText("Parameters").nextElementSibling?.textContent).toBe("Unknown");
	});

	it("shows an error banner when the read fails", async () => {
		inspectionSpy.mockRejectedValue(new Error("boom"));

		renderPanel();

		expect(await screen.findByRole("alert")).toBeTruthy();
	});
});
