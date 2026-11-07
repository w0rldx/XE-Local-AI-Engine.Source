// @vitest-environment jsdom

// The Architecture tab against stubbed routes (MSW): each support level, block selection, the template-to-group join,
// tensor details and a trace laid over the blocks. React Flow paints nothing into jsdom's 0×0 container (see
// graphWorkflows/components/GraphWorkflowRunGraphView.test.tsx), so the mock renders each node as a button and wires
// the one click the view listens for.

import { QueryClient } from "@tanstack/react-query";
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

// Monaco needs a layout engine jsdom lacks; the viewer's contract with the panel is the text, the lines and the reveal.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({
		value,
		highlightLines,
		revealLine,
		"data-testid": testId,
	}: {
		value: string;
		highlightLines?: { start: number; end: number }[];
		revealLine?: number;
		"data-testid"?: string;
	}) => (
		<pre
			data-testid={testId}
			data-highlight={highlightLines?.map((range) => `${range.start}-${range.end}`).join(",")}
			data-reveal={revealLine}
		>
			{value}
		</pre>
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
	suggestedBaseRepo: null,
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
			componentId: "output_norm",
			component: "norm",
			provenance: "read",
			tensors: [tensor("output_norm.weight", "model.norm.weight", "plus_one")],
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
		// No index built yet, unless a test says otherwise.
		http.get(localApiPath("model-lab/architecture/:modelName/pytorch"), () => new HttpResponse(null, { status: 404 })),
		http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [] })),
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

// Opens a select's dropdown and returns its listbox.
async function openOptions(label: RegExp): Promise<HTMLElement> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	return waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
}

// Picks an option and returns every option the dropdown offered.
async function chooseOption(label: RegExp, option: string): Promise<HTMLElement[]> {
	const listbox = await openOptions(label);
	const choice = await within(listbox).findByRole("option", { name: option, hidden: true });
	const offered = within(listbox).getAllByRole("option", { hidden: true });
	fireEvent.click(choice);
	return offered;
}

async function openModel(withRouter = false): Promise<void> {
	renderWithProviders(<ArchitectureExplorer />, { withRouter });
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

	it("offers no Output entry for a file with no output tensors, and says what that may mean without claiming it", async () => {
		stubRoutes({ ...detailed, nonBlock: detailed.nonBlock.filter((group) => group.componentId === "embedding") });
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

	it("opens the embedding's tensors from Embedding, not the output's", async () => {
		stubRoutes(detailed);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Embedding" }));

		expect(screen.getByRole("button", { name: "Embedding" }).getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByRole("button", { name: "Output" }).getAttribute("aria-pressed")).toBe("false");
		expect(screen.getByTestId("model-lab-architecture-group-embedding")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-group-output")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-group-output_norm")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-inferred-badge")).toBeNull();
	});

	it("opens the final norm and the output head from Output, not the embedding", async () => {
		stubRoutes(detailed);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Output" }));

		expect(screen.getByRole("button", { name: "Output" }).getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByTestId("model-lab-architecture-group-output_norm")).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-group-output")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-group-embedding")).toBeNull();
	});

	it("still offers Output for a file with only a final norm, and keeps the shared-weights note", async () => {
		stubRoutes({ ...detailed, nonBlock: detailed.nonBlock.filter((group) => group.componentId !== "output") });
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Output" }));

		expect(screen.getByTestId("model-lab-architecture-group-output_norm")).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-no-output")).toBeTruthy();
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

const modeling = "models/qwen3_5/modeling_qwen3_5.py";
const artifactId = "55555555-5555-4555-8555-555555555555";

const pyModule = (path: string, className: string, classStart: number, classEnd: number, forwardLine: number) => ({
	path,
	class: className,
	file: modeling,
	classStart,
	classEnd,
	forwardLine,
});

const pyIndex = {
	indexId: "index-1",
	modelName: gguf,
	source: { kind: "huggingface", repoId: "Qwen/Qwen3.5-0.8B", revision: "2fc06364715b967f1860", baseArtifactId: null },
	index: {
		transformersVersion: "5.17.0",
		torchVersion: "2.13.0",
		modelType: "qwen3_5",
		textModelType: "qwen3_5_text",
		modelClass: "Qwen3_5ForCausalLM",
		blockPath: "model.layers",
		exportMode: "representative",
		anchoredLines: true,
		blocks: [
			{
				index: 0,
				path: "model.layers.0",
				class: "Qwen3_5DecoderLayer",
				layerType: "linear_attention",
				graphId: "g0",
				modules: [pyModule("model.layers.0.linear_attn", "Qwen3_5GatedDeltaNet", 100, 200, 150)],
			},
			{
				index: 1,
				path: "model.layers.1",
				class: "Qwen3_5DecoderLayer",
				layerType: "full_attention",
				graphId: "g1",
				modules: [
					pyModule("model.layers.1", "Qwen3_5DecoderLayer", 400, 450, 410),
					pyModule("model.layers.1.self_attn", "Qwen3_5Attention", 210, 280, 240),
					pyModule("model.layers.1.mlp", "Qwen3_5MLP", 290, 310, 300),
				],
			},
		],
		outside: [pyModule("model.embed_tokens", "Embedding", 10, 90, 50)],
		outsideGraphId: null,
		graphs: [
			{
				graphId: "g1",
				nodes: [
					{ id: "n0", modulePath: "self_attn.q_proj", file: modeling, line: 245, opCount: 1, label: "q = self.q_proj(x)" },
					{ id: "n1", modulePath: "", file: null, line: null, opCount: 1, label: "rotary" },
				],
				edges: [["n0", "n1"]],
			},
			{ graphId: "g0", nodes: [], edges: [] },
		],
		exportError: null,
		files: [{ file: modeling, sha256: "abc" }],
	},
	join: {
		blocksJoined: true,
		reason: null,
		ggufTrunkBlockCount: 2,
		pyTorchBlockCount: 2,
		components: [
			{ componentId: "blk.1.attention", modulePaths: ["model.layers.1.self_attn"] },
			{ componentId: "embedding", modulePaths: ["model.embed_tokens"] },
		],
		tensors: [],
	},
};

const readyArtifact = {
	id: artifactId,
	repoId: "Qwen/Qwen3.5-0.8B",
	revision: "2fc06364715b967f1860aea9cf38778875588b17",
	status: "Ready",
	totalBytes: 1,
	files: [],
	license: null,
	errorMessage: null,
	version: 1,
	createdAtUtc: 1,
	updatedAtUtc: 1,
	progress: null,
};

// A downloaded checkpoint's pairing check against a 1024-wide GGUF of two trunk blocks; null as for a fit refusal.
const preflight = (hiddenSize: number | null) => ({
	repoId: readyArtifact.repoId,
	revision: null,
	weightBytes: null,
	requiredVramBytes: null,
	cardVramBytes: null,
	license: null,
	isGated: false,
	checkpointHiddenSize: hiddenSize,
	checkpointBlockCount: hiddenSize === null ? null : 2,
	ggufEmbeddingLength: hiddenSize === null ? null : 1024,
	ggufBlockCount: hiddenSize === null ? null : 3,
	steerableBlockCount: hiddenSize === null ? null : 2,
	checkpointLayerTypes: null,
	defaultSteeringLayer: null,
	licenseConfirmationText: null,
	refusal: null,
	refusalMessage: null,
});

// The index GET answers `index` (404 when undefined); every source fetch is recorded as `index file`.
function stubPyTorch(index: unknown, fetched: string[] = []): void {
	server.use(
		http.get(localApiPath("model-lab/architecture/:modelName/pytorch"), () =>
			index === undefined ? new HttpResponse(null, { status: 404 }) : HttpResponse.json(index),
		),
		http.get(localApiPath("model-lab/pytorch-source"), ({ request }) => {
			const query = new URL(request.url).searchParams;
			const file = query.get("file") ?? "";
			fetched.push(`${query.get("index")} ${file}`);
			return HttpResponse.json({ file, sha256: "abc", text: `# source of ${file}` });
		}),
	);
}

async function openBlock(name: string, withRouter = false): Promise<void> {
	await openModel(withRouter);
	fireEvent.click(screen.getByRole("button", { name }));
}

describe("ArchitectureExplorer PyTorch source", () => {
	it("builds from a downloaded checkpoint, offering only ready ones of this GGUF's shape, and shows the index it returns", async () => {
		stubRoutes(detailed);
		stubPyTorch(undefined);
		const bodies: unknown[] = [];
		const otherShape = { ...readyArtifact, id: "77777777-7777-4777-8777-777777777777", repoId: "Qwen/Qwen3.8-27B" };
		const unchecked = { ...readyArtifact, id: "88888888-8888-4888-8888-888888888888", repoId: "org/Unsized" };
		server.use(
			http.get(localApiPath("training/base-artifacts"), () =>
				HttpResponse.json({
					items: [
						readyArtifact,
						{ ...readyArtifact, id: "66666666-6666-4666-8666-666666666666", status: "Downloading" },
						otherShape,
						unchecked,
					],
				}),
			),
			http.post(localApiPath("model-lab/checkpoint-preflight"), async ({ request }) => {
				const { baseArtifactId } = (await request.json()) as { baseArtifactId: string };
				// A fit refusal stops before the shapes are read; that checkpoint stays offered.
				return HttpResponse.json(
					baseArtifactId === unchecked.id
						? { ...preflight(null), refusal: "VramUnknown" }
						: preflight(baseArtifactId === otherShape.id ? 5120 : 1024),
				);
			}),
			http.post(localApiPath("model-lab/architecture/:modelName/pytorch"), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json({
					...pyIndex,
					source: {
						kind: "checkpoint",
						repoId: readyArtifact.repoId,
						revision: readyArtifact.revision,
						baseArtifactId: artifactId,
					},
				});
			}),
		);
		await openBlock("Block 1: Full attention");

		expect((await screen.findByTestId("model-lab-pytorch-not-built")).textContent).toContain(
			"No PyTorch index for this model yet",
		);
		// The list settles as each pairing check answers: the 27B checkpoint's hidden size is not this GGUF's.
		const listbox = await openOptions(/^Checkpoint/);
		await waitFor(() =>
			expect(
				within(listbox)
					.getAllByRole("option", { hidden: true })
					.map((option) => option.textContent),
			).toEqual(["Qwen/Qwen3.5-0.8B @ 2fc06364715b", "org/Unsized @ 2fc06364715b"]),
		);
		fireEvent.click(within(listbox).getByRole("option", { name: "Qwen/Qwen3.5-0.8B @ 2fc06364715b", hidden: true }));
		fireEvent.click(screen.getByRole("button", { name: "Build PyTorch index" }));

		const provenance = await screen.findByTestId("model-lab-pytorch-provenance");
		expect(bodies).toEqual([{ source: "checkpoint", baseArtifactId: artifactId }]);
		expect(provenance.textContent).toBe(
			"transformers 5.17.0, torch 2.13.0 in the installed runtime · PyTorch reference path; GPU runs may use fused kernels · exported one block per layer kind",
		);
		// Not a pill: a wrapped line on a 390 px phone is not clipped by round ends.
		expect(provenance.closest(".mantine-Badge-root")).toBeNull();
		expect(screen.getByTestId("model-lab-pytorch-source-summary").textContent).toBe(
			"Qwen3_5ForCausalLM from the config of the downloaded checkpoint Qwen/Qwen3.5-0.8B @ 2fc06364715b",
		);
		expect(screen.queryByTestId("model-lab-pytorch-not-built")).toBeNull();
		expect(screen.queryByTestId("model-lab-pytorch-call-site-note")).toBeNull();
		expect(screen.queryByTestId("model-lab-pytorch-declared-class")).toBeNull();
	});

	it("opens the source picker on the source the current index was built from", async () => {
		stubRoutes({ ...detailed, suggestedBaseRepo: "Qwen/Other" });
		stubPyTorch(pyIndex);
		await openBlock("Block 1: Full attention");

		expect((await screen.findByTestId("model-lab-pytorch-source-summary")).textContent).toBe(
			"Qwen3_5ForCausalLM from the Hugging Face config.json of Qwen/Qwen3.5-0.8B @ 2fc06364715b",
		);
		expect((screen.getByRole("radio", { name: "Hugging Face config.json" }) as HTMLInputElement).checked).toBe(true);
		expect((screen.getByRole("textbox", { name: "Repository" }) as HTMLInputElement).value).toBe("Qwen/Qwen3.5-0.8B");
		expect((screen.getByRole("textbox", { name: "Revision (optional)" }) as HTMLInputElement).value).toBe("2fc06364715b967f1860");
	});

	it("says when the config declares another class than the one the index shows", async () => {
		stubRoutes(detailed);
		stubPyTorch({
			...pyIndex,
			index: {
				...pyIndex.index,
				modelClass: "XLMRobertaForCausalLM",
				declaredArchitectures: ["XLMRobertaForSequenceClassification"],
			},
		});
		await openBlock("Block 1: Full attention");

		expect((await screen.findByTestId("model-lab-pytorch-declared-class")).textContent).toBe(
			"The config declares XLMRobertaForSequenceClassification; shown through XLMRobertaForCausalLM, so its head differs.",
		);
	});

	it("builds from a Hugging Face config, with the repository prefilled from the file's base model", async () => {
		stubRoutes({ ...detailed, suggestedBaseRepo: "Qwen/Qwen3.5-0.8B-Base" });
		stubPyTorch(undefined);
		const bodies: unknown[] = [];
		server.use(
			http.post(localApiPath("model-lab/architecture/:modelName/pytorch"), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json(pyIndex);
			}),
		);
		await openBlock("Block 1: Full attention");

		fireEvent.click(await screen.findByRole("radio", { name: "Hugging Face config.json" }));
		expect((screen.getByRole("textbox", { name: "Repository" }) as HTMLInputElement).value).toBe("Qwen/Qwen3.5-0.8B-Base");
		fireEvent.change(screen.getByRole("textbox", { name: "Revision (optional)" }), { target: { value: " main " } });
		fireEvent.click(screen.getByRole("button", { name: "Build PyTorch index" }));

		expect(await screen.findByTestId("model-lab-pytorch-provenance")).toBeTruthy();
		expect(bodies).toEqual([{ source: "huggingface", repoId: "Qwen/Qwen3.5-0.8B-Base", revision: "main" }]);
	});

	it("says a missing runtime is installed on the Training page, and links there", async () => {
		stubRoutes({ ...detailed, suggestedBaseRepo: "Qwen/Qwen3.5-0.8B" });
		stubPyTorch(undefined);
		server.use(
			http.post(localApiPath("model-lab/architecture/:modelName/pytorch"), () =>
				HttpResponse.json({ reason: "RuntimeUnavailable", message: "server text" }, { status: 409 }),
			),
		);
		// The refusal links to the Training page, which needs router context.
		await openBlock("Block 1: Full attention", true);

		fireEvent.click(await screen.findByRole("radio", { name: "Hugging Face config.json" }));
		fireEvent.click(screen.getByRole("button", { name: "Build PyTorch index" }));

		const refusal = await screen.findByTestId("model-lab-pytorch-refusal");
		expect(refusal.textContent).toContain("The training runtime is not installed or not ready. Install it on the Training page.");
		expect(within(refusal).getByRole("link", { name: "Open the Training page" }).getAttribute("href")).toBe("/training");
	});

	it("opens a graph node's line, a group's class with forward revealed, and says when a group has no code link", async () => {
		stubRoutes(detailed);
		const fetched: string[] = [];
		stubPyTorch(pyIndex, fetched);
		await openBlock("Block 1: Full attention");
		await screen.findByTestId("model-lab-pytorch-provenance");

		fireEvent.click(screen.getByTestId("react-flow-node-n0"));
		const code = await screen.findByTestId("model-lab-pytorch-code");
		expect(code.textContent).toBe(`# source of ${modeling}`);
		expect(code.dataset["highlight"]).toBe("245-245");
		expect(code.dataset["reveal"]).toBe("245");
		expect(screen.getByTestId("model-lab-pytorch-file").textContent).toBe(`${modeling}, line 245`);
		expect(fetched).toEqual([`index-1 ${modeling}`]);

		// An op of the block's own forward is named by the block's class, not left without a prefix.
		expect(screen.getByTestId("react-flow-node-n1").textContent).toBe("Qwen3_5DecoderLayer: rotary");
		// A node with no recorded line opens nothing: the last selection stays.
		fireEvent.click(screen.getByTestId("react-flow-node-n1"));
		expect(screen.getByTestId("model-lab-pytorch-code").dataset["highlight"]).toBe("245-245");

		fireEvent.click(screen.getByRole("button", { name: /^Attention/ }));
		expect(screen.getByTestId("model-lab-pytorch-code").dataset["highlight"]).toBe("210-280");
		expect(screen.getByTestId("model-lab-pytorch-file").textContent).toBe(`${modeling}, lines 210–280`);
		expect(screen.getByTestId("model-lab-pytorch-code").dataset["reveal"]).toBe("240");
		// The same file is not fetched again.
		expect(fetched).toHaveLength(1);

		// A llama.cpp template node links at component level only: the FFN group has no PyTorch link here.
		fireEvent.click(screen.getByTestId("react-flow-node-ffn"));
		expect(screen.getByTestId("model-lab-pytorch-no-link").textContent).toBe("No code link for this selection.");

		fireEvent.click(within(screen.getByTestId("model-lab-pytorch-modules")).getByRole("button", { name: "mlp" }));
		expect(screen.getByTestId("model-lab-pytorch-code").dataset["highlight"]).toBe("290-310");

		fireEvent.click(within(screen.getByTestId("model-lab-pytorch-modules")).getByRole("button", { name: "(block)" }));
		expect(screen.getByTestId("model-lab-pytorch-code").dataset["highlight"]).toBe("400-450");
	});

	it("fetches a file again after a rebuild lists a new hash for it, and only that file", async () => {
		const linear = "torch:nn/modules/linear.py";
		const withHashes = (linearHash: string) => ({
			...pyIndex,
			index: {
				...pyIndex.index,
				blocks: pyIndex.index.blocks.map((block) => ({
					...block,
					modules: block.modules.map((module) => (module.path === "model.layers.1.mlp" ? { ...module, file: linear } : module)),
				})),
				files: [
					{ file: modeling, sha256: "abc" },
					{ file: linear, sha256: linearHash },
				],
			},
		});
		stubRoutes(detailed);
		const fetched: string[] = [];
		let rebuilt = false;
		stubPyTorch(withHashes("old"));
		server.use(
			http.get(localApiPath("model-lab/pytorch-source"), ({ request }) => {
				const file = new URL(request.url).searchParams.get("file") ?? "";
				fetched.push(file);
				if (file === linear && !rebuilt) {
					return HttpResponse.json({ reason: "SourceChanged", message: "server text" }, { status: 409 });
				}
				return HttpResponse.json({ file, sha256: "abc", text: rebuilt ? `# rebuilt ${file}` : `# source of ${file}` });
			}),
			// The same config on the same runtime: the same indexId, with the changed file's new hash.
			http.post(localApiPath("model-lab/architecture/:modelName/pytorch"), () => {
				rebuilt = true;
				return HttpResponse.json(withHashes("new"));
			}),
		);
		// The app's cache keeps an unmounted file for minutes; the suite's default client drops it at once.
		renderWithProviders(<ArchitectureExplorer />, {
			queryClient: new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } }),
		});
		await chooseOption(/Installed model/, gguf);
		fireEvent.click(await screen.findByRole("button", { name: "Block 1: Full attention" }));
		await screen.findByTestId("model-lab-pytorch-provenance");

		fireEvent.click(screen.getByTestId("react-flow-node-n0"));
		expect((await screen.findByTestId("model-lab-pytorch-code")).textContent).toBe(`# source of ${modeling}`);
		fireEvent.click(within(screen.getByTestId("model-lab-pytorch-modules")).getByRole("button", { name: "mlp" }));
		expect((await screen.findByTestId("model-lab-pytorch-source-failed")).textContent).toBe(
			"The file changed since the index was built. Build the index again.",
		);

		fireEvent.click(screen.getByRole("button", { name: "Build PyTorch index" }));

		expect((await screen.findByTestId("model-lab-pytorch-code")).textContent).toBe(`# rebuilt ${linear}`);
		expect(screen.queryByTestId("model-lab-pytorch-source-failed")).toBeNull();
		fireEvent.click(screen.getByTestId("react-flow-node-n0"));
		expect(screen.getByTestId("model-lab-pytorch-code").textContent).toBe(`# source of ${modeling}`);
		expect(fetched).toEqual([modeling, linear, linear]);
	});

	it("keeps module and class links when the export failed, and shows why there is no graph", async () => {
		stubRoutes(detailed);
		stubPyTorch({
			...pyIndex,
			index: { ...pyIndex.index, graphs: [], exportError: "RuntimeError: meta tensor", anchoredLines: false },
		});
		await openBlock("Block 1: Full attention");

		expect((await screen.findByTestId("model-lab-pytorch-export-error")).textContent).toContain("RuntimeError: meta tensor");
		expect(screen.getByTestId("model-lab-pytorch-call-site-note")).toBeTruthy();
		expect(screen.queryByTestId("react-flow-node-n0")).toBeNull();

		fireEvent.click(within(screen.getByTestId("model-lab-pytorch-modules")).getByRole("button", { name: "self_attn" }));
		expect((await screen.findByTestId("model-lab-pytorch-code")).dataset["highlight"]).toBe("210-280");
	});

	it("does not pair blocks on a count mismatch, says why, and still shows a chosen PyTorch block", async () => {
		stubRoutes(detailed);
		stubPyTorch({
			...pyIndex,
			join: { ...pyIndex.join, blocksJoined: false, reason: "blockCountMismatch", pyTorchBlockCount: 3, components: [] },
		});
		await openBlock("Block 1: Full attention");

		expect((await screen.findByTestId("model-lab-pytorch-join-reason")).textContent).toBe(
			"The GGUF has 2 blocks and the PyTorch model 3, so they are not paired by index. Choose a PyTorch block to view.",
		);
		expect(screen.queryByTestId("react-flow-node-n0")).toBeNull();

		await chooseOption(/PyTorch block/, "1: Qwen3_5DecoderLayer (full_attention)");
		expect(screen.getByTestId("react-flow-node-n0")).toBeTruthy();
		// Without a pairing, a GGUF group links to no PyTorch module.
		fireEvent.click(screen.getByRole("button", { name: /^Attention/ }));
		expect(screen.getByTestId("model-lab-pytorch-no-link")).toBeTruthy();
	});

	it("says the MTP block has no PyTorch layer", async () => {
		stubRoutes(detailed);
		stubPyTorch(pyIndex);
		await openBlock("Block 2: MTP, not traceable");

		expect((await screen.findByTestId("model-lab-pytorch-mtp")).textContent).toBe("The MTP block has no PyTorch layer.");
		expect(screen.queryByTestId("model-lab-pytorch-modules")).toBeNull();
	});

	it("links the embedding group to its module outside the blocks", async () => {
		stubRoutes(detailed);
		stubPyTorch(pyIndex);
		await openBlock("Embedding");
		await screen.findByTestId("model-lab-pytorch-provenance");

		fireEvent.click(screen.getByRole("button", { name: /^Embedding embedding/ }));
		expect((await screen.findByTestId("model-lab-pytorch-code")).dataset["highlight"]).toBe("10-90");
	});
});
