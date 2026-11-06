// @vitest-environment jsdom

// The Architecture tab against stubbed routes (MSW): each support level, block selection, the template-to-group join,
// tensor details and a trace laid over the blocks. React Flow paints nothing into jsdom's 0×0 container (see
// graphWorkflows/components/GraphWorkflowRunGraphView.test.tsx), so the mock renders each node as a button and wires
// the one click the view listens for.

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import type { Node } from "@xyflow/react";
import { HttpResponse, http } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { ArchitectureExplorer } from "@/features/modelLab/components/ArchitectureExplorer";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

vi.mock("@xyflow/react", () => ({
	Background: () => null,
	Controls: () => null,
	ReactFlow: ({
		nodes,
		onNodeClick,
		fitViewOptions,
	}: {
		nodes: Node<{ label: string }>[];
		onNodeClick?: (event: unknown, node: Node) => void;
		fitViewOptions?: { minZoom?: number };
		children?: ReactNode;
	}) => (
		<div data-testid="react-flow" data-fit-min-zoom={fitViewOptions?.minZoom}>
			{nodes.map((node) => (
				<button
					key={node.id}
					type="button"
					data-testid={`react-flow-node-${node.id}`}
					data-selected={String(node.selected)}
					onClick={(event) => onNodeClick?.(event, node)}
				>
					{node.data.label}
				</button>
			))}
		</div>
	),
}));

setupMswServer();

const gguf = "org/Tiny-GGUF:Q4_K_M";
const traceId = "11111111-1111-4111-8111-111111111111";

const tensor = (ggufName: string, hfName: string | null, valueRelation: string | null) => ({
	ggufName,
	hfName,
	shape: [1024, 256],
	ggmlType: "Q4_K",
	byteSize: 147_456,
	valueRelation,
});

const detailed = {
	descriptorVersion: 1,
	modelName: gguf,
	architecture: "qwen35",
	support: "detailed",
	readFailure: null,
	blocks: [
		{
			index: 0,
			kind: "linearAttention",
			byteSize: 1,
			groups: [
				{
					componentId: "blk.0.ssm",
					component: "ssm",
					provenance: "read",
					tensors: [tensor("blk.0.ssm_out.weight", "model.layers.0.linear_attn.out_proj.weight", "reordered")],
				},
			],
		},
		{
			index: 1,
			kind: "fullAttention",
			byteSize: 1,
			groups: [
				{
					componentId: "blk.1.attention",
					component: "attention",
					provenance: "read",
					tensors: [tensor("blk.1.attn_q.weight", "model.layers.1.self_attn.q_proj.weight", "quantized")],
				},
				{
					componentId: "blk.1.ffn",
					component: "ffn",
					provenance: "read",
					tensors: [tensor("blk.1.ffn_down.weight", null, null)],
				},
			],
		},
		{
			index: 2,
			kind: "mtp",
			byteSize: 1,
			groups: [
				{
					componentId: "blk.2.mtp",
					component: "mtp",
					provenance: "read",
					tensors: [tensor("blk.2.nextn.eh_proj.weight", null, null)],
				},
			],
		},
	],
	templates: [
		{
			kind: "fullAttention",
			provenance: "inferred",
			source: "llama.cpp src/models/qwen35.cpp @ b12014",
			nodes: [
				{ id: "input", label: "Residual in", component: null },
				{ id: "attn", label: "Gated attention", component: "attention" },
				{ id: "ffn", label: "SwiGLU FFN", component: "ffn" },
			],
			edges: [
				{ from: "input", to: "attn" },
				{ from: "attn", to: "ffn" },
			],
		},
	],
	nonBlock: [
		{
			componentId: "embedding",
			component: "embedding",
			provenance: "read",
			tensors: [tensor("token_embd.weight", "model.embed_tokens.weight", "quantized")],
		},
		{
			componentId: "output",
			component: "output",
			provenance: "read",
			tensors: [tensor("output.weight", "lm_head.weight", "quantized")],
		},
	],
};

function stubRoutes(descriptor: Record<string, unknown>, experiments: unknown[] = []): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({
				isAvailable: true,
				items: [
					{
						modelName: gguf,
						displayLabel: null,
						isSelected: false,
						kind: "Chat",
						detectedKind: "Chat",
						capabilities: [],
						isReasoningCapable: false,
						isToolCapable: true,
						isOverridden: false,
						provider: "llamacpp",
					},
				],
			}),
		),
		http.get(localApiPath("model-lab/architecture/:modelName"), () => HttpResponse.json(descriptor)),
		http.get(localApiPath("model-lab/experiments"), () =>
			HttpResponse.json({
				platformSupported: true,
				unsupportedMessage: null,
				activeExperimentId: null,
				maxRetainedExperiments: 20,
				experiments,
			}),
		),
	);
}

// Picks an option and returns every option the dropdown offered.
async function chooseOption(label: RegExp, option: string): Promise<HTMLElement[]> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	const choice = await within(listbox).findByRole("option", { name: option, hidden: true });
	const offered = within(listbox).getAllByRole("option", { hidden: true });
	fireEvent.click(choice);
	return offered;
}

async function openModel(): Promise<void> {
	renderWithProviders(<ArchitectureExplorer />);
	await chooseOption(/Installed model/, gguf);
	await screen.findByTestId("model-lab-architecture-overview");
}

describe("ArchitectureExplorer", () => {
	it("lays out embedding, every block and the output, with the MTP block marked not traceable", async () => {
		stubRoutes(detailed);
		await openModel();

		expect(screen.getByRole("button", { name: "Embedding" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Output" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Block 0: Linear attention" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Block 1: Full attention" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Block 2: MTP, not traceable" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-overview-only")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-no-output")).toBeNull();
	});

	it("offers no Output entry for a file with no output tensor, and says what that may mean without claiming it", async () => {
		stubRoutes({ ...detailed, nonBlock: detailed.nonBlock.filter((group) => group.component !== "output") });
		await openModel();

		expect(screen.queryByRole("button", { name: "Output" })).toBeNull();
		expect(screen.getByTestId("model-lab-architecture-no-output").textContent).toBe(
			"No separate output tensor in this file; the output may share the embedding weights.",
		);
		expect(screen.getByRole("button", { name: "Embedding" })).toBeTruthy();
	});

	it("shows a block's groups as read and its template as inferred, and a template node selects its group", async () => {
		stubRoutes(detailed);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));

		expect(screen.getByTestId("model-lab-architecture-read-badge").textContent).toBe("Read from the file");
		const inferred = screen.getByTestId("model-lab-architecture-inferred-badge");
		expect(inferred.textContent).toBe("Inferred from llama.cpp (llama.cpp src/models/qwen35.cpp @ b12014)");
		// A badge clips its label to one line by default, which cut the pinned tag off on a phone.
		expect((inferred.querySelector(".mantine-Badge-label") as HTMLElement).style.whiteSpace).toBe("normal");
		// The first fit stops at a zoom where 12 px node text still reads (9.6 px); past that the graph pans.
		expect(Number(screen.getByTestId("react-flow").dataset["fitMinZoom"])).toBeGreaterThanOrEqual(0.8);
		const group = screen.getByTestId("model-lab-architecture-group-blk.1.attention");
		expect(group.dataset["selected"]).toBe("false");

		fireEvent.click(screen.getByTestId("react-flow-node-attn"));

		expect(group.dataset["selected"]).toBe("true");
		expect(screen.getByTestId("react-flow-node-attn").dataset["selected"]).toBe("true");
		// A node with no tensors selects nothing.
		fireEvent.click(screen.getByTestId("react-flow-node-input"));
		expect(group.dataset["selected"]).toBe("true");
	});

	it("draws no template for a block kind it has none for", async () => {
		stubRoutes(detailed);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));

		expect(screen.getByTestId("model-lab-architecture-read-badge")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-inferred-badge")).toBeNull();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("shows a tensor's names, shape, type, size and value relation, in words", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));

		fireEvent.click(screen.getByRole("button", { name: "attn_q.weight" }));
		const details = screen.getByTestId("model-lab-architecture-tensor");
		expect(details.textContent).toContain("blk.1.attn_q.weight");
		expect(details.textContent).toContain("model.layers.1.self_attn.q_proj.weight");
		expect(details.textContent).toContain("1024 × 256");
		expect(details.textContent).toContain("Q4_K");
		expect(details.textContent).toContain("Quantized from the Hugging Face values");

		fireEvent.click(screen.getByRole("button", { name: "ffn_down.weight" }));
		const unmapped = within(screen.getByTestId("model-lab-architecture-tensor"));
		expect(unmapped.getByText("None")).toBeTruthy();
		expect(unmapped.getByText("Not stated")).toBeTruthy();

		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		fireEvent.click(screen.getByRole("button", { name: "ssm_out.weight" }));
		expect(screen.getByTestId("model-lab-architecture-tensor").textContent).toContain(
			"Value heads reordered from the Hugging Face tensor, so values sit in another order",
		);
	});

	it("shows the tensors outside the blocks from the embedding or the output", async () => {
		stubRoutes(detailed);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Output" }));

		expect(screen.getByTestId("model-lab-architecture-detail").textContent).toContain("Outside blocks");
		expect(screen.getByTestId("model-lab-architecture-group-embedding")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-inferred-badge")).toBeNull();
	});

	it("shows groups only for an overview, and says no operation graph is shown", async () => {
		stubRoutes({ ...detailed, architecture: "llama", support: "overview", templates: detailed.templates });
		await openModel();

		expect(screen.getByTestId("model-lab-architecture-overview-only").textContent).toContain(
			"No operation graph is shown for the llama architecture",
		);
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		expect(screen.getByTestId("model-lab-architecture-read-badge")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-inferred-badge")).toBeNull();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("shows an unreadable file's read failure and no structure", async () => {
		stubRoutes({ ...detailed, support: "unreadable", readFailure: "truncated", blocks: [], templates: [], nonBlock: [] });
		renderWithProviders(<ArchitectureExplorer />);
		await chooseOption(/Installed model/, gguf);

		expect((await screen.findByTestId("model-lab-architecture-unreadable")).textContent).toContain("(truncated)");
		expect(screen.queryByTestId("model-lab-architecture-overview")).toBeNull();
	});

	it("lays a finished trace of this model over the blocks it measured, and says which it did not", async () => {
		const row = {
			repoId: "Qwen/Qwen3.5-0.8B",
			revision: "rev",
			prompt: "Why is\nthe sky blue?",
			errorMessage: null,
			traceByteCount: 10,
			result: null,
			createdAtUtc: "2026-11-06T00:00:00Z",
			updatedAtUtc: "2026-11-06T00:00:00Z",
		};
		stubRoutes(detailed, [
			{ ...row, id: traceId, kind: "Trace", status: "Succeeded", installedModelName: gguf },
			{ ...row, id: "22222222-2222-4222-8222-222222222222", kind: "Steering", status: "Succeeded", installedModelName: gguf },
			{ ...row, id: "33333333-3333-4333-8333-333333333333", kind: "Trace", status: "Failed", installedModelName: gguf },
			{ ...row, id: "44444444-4444-4444-8444-444444444444", kind: "Trace", status: "Succeeded", installedModelName: "other" },
		]);
		server.use(
			http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () =>
				HttpResponse.json({
					records: [
						{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7, 3], topValues: [1, 1] },
						{ step: 1, layer: 1, kind: "F", norm: 4, percentiles: [0, 1, 9], topIndices: [7, 9], topValues: [1, 1] },
					],
					truncated: false,
					droppedRecords: 0,
					byteCap: 1000,
				}),
			),
		);
		await openModel();

		const options = await chooseOption(
			/Trace to lay over the blocks/,
			// The prompt tells two traces of one model apart; the date follows the page's language.
			`Why is the sky blue? · Qwen/Qwen3.5-0.8B · ${formatTimestamp(row.createdAtUtc)}`,
		);

		// Only the finished trace of this model is offered: not the steering run, the failed trace or another model's.
		expect(options).toHaveLength(1);
		// The MTP block has no checkpoint layer, so two of the three blocks could have been traced.
		expect((await screen.findByTestId("model-lab-architecture-traced-blocks")).textContent).toBe("Traced blocks: 1 (1 of 2).");

		expect(screen.getByText(/Measured on the Hugging Face checkpoint, joined by block index/)).toBeTruthy();
		const traced = await screen.findByRole("button", { name: "Block 1: Full attention, traced" });
		expect(screen.getByTestId("model-lab-architecture-block-0").dataset["traced"]).toBe("false");

		fireEvent.click(traced);
		expect(screen.getByTestId("model-lab-architecture-trace-summary").textContent).toBe(
			"Mean norm 3, highest p99 9. Most frequent top dimensions: 7, 3, 9.",
		);
		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		expect(screen.getByTestId("model-lab-architecture-not-traced")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Block 2: MTP, not traceable" }));
		expect(screen.getByTestId("model-lab-architecture-untraceable")).toBeTruthy();
	});
});
