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

function renderPanel(compareCandidates: string[] = []) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ModelInspectPanel modelName="org/Tiny-GGUF:Q4_K_M" compareCandidates={compareCandidates} />
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
		// The compare Select's dropdown scrolls in a ScrollArea, which observes its size.
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();
				unobserve = vi.fn();
				disconnect = vi.fn();
			},
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
		fireEvent.click(within(block0).getByText("Block 0: Other"));
		expect(await within(block0).findByText("attn_norm.weight")).toBeTruthy();

		const block1 = screen.getByTestId("model-inspect-block-1");
		expect(within(block1).getByText("1 tensors · Unknown")).toBeTruthy();
		fireEvent.click(within(block1).getByText("Block 1: Other"));
		expect(await within(block1).findByText("type 99")).toBeTruthy();
	});

	it("colours a cell per block kind, labels it and counts the kinds in the legend", async () => {
		inspectionSpy.mockResolvedValue({
			...inspection,
			blocks: [
				{ index: 0, byteSize: 1024, tensors: [tensor("blk.0.ssm_a.weight", [256], "F32", 1024)] },
				{ index: 1, byteSize: 1024, tensors: [tensor("blk.1.ssm_a.weight", [256], "F32", 1024)] },
				{ index: 2, byteSize: 1024, tensors: [tensor("blk.2.attn_q.weight", [256], "F32", 1024)] },
				{ index: 3, byteSize: 1024, tensors: [tensor("blk.3.nextn.enorm.weight", [256], "F32", 1024)] },
			],
		});

		renderPanel();

		const strip = await screen.findByTestId("model-inspect-block-strip");
		const mtpCell = within(strip).getByRole("img", { name: "Block 3: MTP" });
		expect(within(strip).getByRole("img", { name: "Block 0: Linear attention" })).toBeTruthy();
		expect(within(within(strip).getByTestId("model-inspect-kind-linearAttention")).getByText("2")).toBeTruthy();
		expect(within(within(strip).getByTestId("model-inspect-kind-fullAttention")).getByText("1")).toBeTruthy();
		expect(within(strip).queryByTestId("model-inspect-kind-other")).toBeNull();

		fireEvent.mouseEnter(mtpCell);
		expect((await screen.findByRole("tooltip")).textContent).toBe("Block 3: MTP");
	});

	it("marks expert blocks on top of their kind and counts them in the legend", async () => {
		inspectionSpy.mockResolvedValue({
			...inspection,
			blocks: [
				{
					index: 0,
					byteSize: 1024,
					tensors: [tensor("blk.0.ssm_a.weight", [256], "F32", 1024), tensor("blk.0.ffn_up_exps.weight", [256], "F32", 1024)],
				},
				{
					index: 1,
					byteSize: 1024,
					tensors: [tensor("blk.1.attn_q.weight", [256], "F32", 1024), tensor("blk.1.ffn_up_exps.weight", [256], "F32", 1024)],
				},
				{ index: 2, byteSize: 1024, tensors: [tensor("blk.2.attn_q.weight", [256], "F32", 1024)] },
				{ index: 3, byteSize: 1024, tensors: [tensor("blk.3.ffn_up_exps.weight", [256], "F32", 1024)] },
			],
		});

		renderPanel();

		const strip = await screen.findByTestId("model-inspect-block-strip");
		const expertCell = within(strip).getByRole("img", { name: "Block 0: Linear attention, MoE expert" });
		const denseCell = within(strip).getByRole("img", { name: "Block 2: Full attention" });
		expect(within(strip).getByRole("img", { name: "Block 1: Full attention, MoE expert" })).toBeTruthy();
		expect(within(strip).getByRole("img", { name: "Block 3: Other, MoE expert" })).toBeTruthy();
		expect(expertCell.getAttribute("style")).toContain("box-shadow");
		expect(denseCell.getAttribute("style")).not.toContain("box-shadow");
		expect(within(within(strip).getByTestId("model-inspect-kind-fullAttention")).getByText("2")).toBeTruthy();
		expect(within(within(strip).getByTestId("model-inspect-kind-moeExpert")).getByText("3")).toBeTruthy();

		// The strip's cells are not focusable; each block's accordion row names the same kind and expert state.
		expect(expertCell.getAttribute("tabindex")).toBeNull();
		expect(screen.getByRole("button", { name: /Block 0: Linear attention, MoE expert/ })).toBeTruthy();
		expect(screen.getByRole("button", { name: /Block 2: Full attention/ })).toBeTruthy();
	});

	it("has no expert legend entry when no block has experts", async () => {
		inspectionSpy.mockResolvedValue(inspection);

		renderPanel();

		const strip = await screen.findByTestId("model-inspect-block-strip");
		expect(within(strip).queryByTestId("model-inspect-kind-moeExpert")).toBeNull();
		expect(within(strip).getByRole("img", { name: "Block 0: Other" }).getAttribute("style")).toContain("--mantine-color-gray-6");
	});

	it("totals the bytes per type over all tensors and states what it cannot size", async () => {
		inspectionSpy.mockResolvedValue({
			...inspection,
			blocks: [
				{
					index: 0,
					byteSize: null,
					tensors: [
						{ ...tensor("blk.0.a.weight", [256], "F32", 1024), ggmlTypeId: 0 },
						{ ...tensor("blk.0.b.weight", [256], "F32", null), ggmlTypeId: 0 },
					],
				},
				{ index: 1, byteSize: null, tensors: [tensor("blk.1.future.weight", [256, 4], null, null)] },
			],
			nonBlockTensors: [tensor("token_embd.weight", [4096, 248320], "Q4_K", 3072)],
		});

		renderPanel();

		const section = await screen.findByTestId("model-inspect-type-bytes");
		const cells = (id: number) =>
			within(within(section).getByTestId(`model-inspect-type-${id}`))
				.getAllByRole("cell")
				.map((cell) => cell.textContent);
		expect(cells(12)).toEqual(["Q4_K", "1", "3 KB", "75.0%"]);
		expect(cells(0)).toEqual(["F32", "2", "1 KB + 1 of unknown size", "25.0%"]);
		expect(cells(99)).toEqual(["type 99", "1", "Unknown", "Unknown"]);
	});

	it("hides the insights when the read failed or no tensor was listed", async () => {
		inspectionSpy.mockResolvedValue({ ...inspection, readFailure: "truncated" });
		renderPanel();
		await screen.findByTestId("model-inspect-panel");
		expect(screen.queryByTestId("model-inspect-block-strip")).toBeNull();
		expect(screen.queryByTestId("model-inspect-type-bytes")).toBeNull();
		cleanup();

		inspectionSpy.mockResolvedValue({ ...inspection, blocks: [], nonBlockTensors: [] });
		renderPanel();
		await screen.findByTestId("model-inspect-panel");
		expect(screen.queryByTestId("model-inspect-block-strip")).toBeNull();
		expect(screen.queryByTestId("model-inspect-type-bytes")).toBeNull();
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

	describe("comparison", () => {
		const q3 = "org/Tiny-GGUF:Q3_K_XL";
		const blocks = [
			{
				index: 0,
				byteSize: 3072,
				tensors: [tensor("blk.0.attn_q.weight", [256], "Q4_K", 2048), tensor("blk.0.same.weight", [256], "F32", 1024)],
			},
			{ index: 1, byteSize: 1024, tensors: [tensor("blk.1.same.weight", [256], "F32", 1024)] },
		];
		const nonBlockTensors = [tensor("token_embd.weight", [256], "F32", 1024)];
		const other = {
			...inspection,
			modelName: q3,
			blocks: [
				{
					index: 0,
					byteSize: 2048,
					tensors: [
						{ ...tensor("blk.0.attn_q.weight", [256], "Q3_K", 1024), ggmlTypeId: 11 },
						tensor("blk.0.same.weight", [256], "F32", 1024),
					],
				},
				blocks[1],
			],
			nonBlockTensors,
		};

		function stubInspections(otherResult: () => unknown = () => other) {
			inspectionSpy.mockImplementation((modelName: string) =>
				modelName === q3 ? otherResult() : { ...inspection, blocks, nonBlockTensors },
			);
		}

		async function pick() {
			const select = (await screen.findByTestId("model-inspect-compare-select")) as HTMLInputElement;
			fireEvent.click(select);
			fireEvent.click(await screen.findByRole("option", { name: "Q3_K_XL", hidden: true }));
		}

		it("is hidden with no other variant", async () => {
			stubInspections();

			renderPanel();

			await screen.findByTestId("model-inspect-panel");
			expect(screen.queryByTestId("model-inspect-compare")).toBeNull();
		});

		it("reads the other file only once it is picked, and summarises the difference", async () => {
			stubInspections();

			renderPanel([q3]);

			await screen.findByTestId("model-inspect-compare");
			expect(inspectionSpy).not.toHaveBeenCalledWith(q3);

			await pick();

			const result = await screen.findByTestId("model-inspect-compare-result");
			expect(inspectionSpy).toHaveBeenCalledWith(q3);
			const value = (label: string) => within(result).getByText(label).nextElementSibling?.textContent;
			expect(value("Q4_K_M")).toBe("5 KB");
			expect(value("Q3_K_XL")).toBe("4 KB");
			expect(value("Difference")).toBe("−1 KB");
			expect(value("Tensors that changed type")).toBe("1");
		});

		it("expands a block to its changed tensors only, and leaves an unchanged block closed", async () => {
			stubInspections();

			renderPanel([q3]);
			await pick();

			const block0 = await screen.findByTestId("model-inspect-compare-blk-0");
			expect(within(block0).getByText("−1 KB · 1 changed")).toBeTruthy();
			expect(within(block0).queryByText("attn_q.weight")).toBeNull();
			const block1 = screen.getByTestId("model-inspect-compare-blk-1");
			expect(within(block1).getByText("0 B · 0 changed")).toBeTruthy();
			expect(within(block1).getByRole("button").hasAttribute("disabled")).toBe(true);

			fireEvent.click(within(block0).getByText("Block 0"));

			expect(await within(block0).findByText("attn_q.weight")).toBeTruthy();
			expect(within(block0).getByText("Q4_K → Q3_K")).toBeTruthy();
			expect(within(block0).getByText("2 KB → 1 KB")).toBeTruthy();
			expect(within(block0).queryByText("same.weight")).toBeNull();
		});

		it("shows a failed read of the other file and no comparison", async () => {
			stubInspections(() => ({ ...other, readFailure: "truncated" }));

			renderPanel([q3]);
			await pick();

			expect((await screen.findByTestId("model-inspect-compare-failure")).textContent).toBe(
				`${q3} could not be read completely (truncated), so nothing is compared.`,
			);
			expect(screen.queryByTestId("model-inspect-compare-result")).toBeNull();
		});

		it("shows a failed read of this file and no comparison", async () => {
			inspectionSpy.mockImplementation((modelName: string) =>
				modelName === q3 ? other : { ...inspection, readFailure: "malformed", blocks: [], nonBlockTensors: [] },
			);

			renderPanel([q3]);
			await pick();

			expect((await screen.findByTestId("model-inspect-compare-failure")).textContent).toContain("org/Tiny-GGUF:Q4_K_M");
			expect(screen.queryByTestId("model-inspect-compare-result")).toBeNull();
		});

		it("shows a request error for the other file and no comparison", async () => {
			stubInspections(() => {
				throw new Error("boom");
			});

			renderPanel([q3]);
			await pick();

			const compare = screen.getByTestId("model-inspect-compare");
			expect(await within(compare).findByRole("alert")).toBeTruthy();
			expect(screen.queryByTestId("model-inspect-compare-result")).toBeNull();
		});

		it("states that a split model is compared by its first file only", async () => {
			stubInspections(() => ({ ...other, splitCount: 2 }));

			renderPanel([q3]);
			await pick();

			expect(await screen.findByText("A split model is compared by its first file only.")).toBeTruthy();
		});
	});
});
