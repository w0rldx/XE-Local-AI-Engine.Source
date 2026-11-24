// @vitest-environment jsdom

// The Architecture tab against stubbed routes (MSW): each support level, block selection, the template-to-group join,
// tensor details and a trace laid over the blocks. React Flow paints nothing into jsdom's 0×0 container (see
// graphWorkflows/components/GraphWorkflowRunGraphView.test.tsx), so the mock renders each node as a button and wires
// the one click the view listens for.

import { QueryClient } from "@tanstack/react-query";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import type { Node } from "@xyflow/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { type ReactNode, useState } from "react";
import { beforeAll, describe, expect, it, vi } from "vitest";

import {
	getModelLabExperimentRoutingQueryKey,
	getModelLabExperimentTraceQueryKey,
	listModelLabExperimentsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { zGetModelLabArchitectureResponse } from "@/core/api/generated/zod.gen";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { ArchitectureExplorer } from "@/features/modelLab/components/ArchitectureExplorer";
import modelLabEn from "@/features/modelLab/locales/model-lab-en.json";
import {
	applyModelLabPatch,
	jumpPatch,
	type ModelLabJump,
	type ModelLabPatch,
} from "@/features/modelLab/models/ModelLabNavigation";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Counts React Flow mounts: the real one fits the view only on mount, so a new mount id is a new fit.
const flowMounts = vi.hoisted(() => ({ count: 0 }));

vi.mock("@xyflow/react", () => ({
	Background: () => null,
	Controls: () => null,
	ReactFlow: function MockReactFlow({
		nodes,
		onNodeClick,
		fitViewOptions,
	}: {
		nodes: Node<{ label: string }>[];
		onNodeClick?: (event: unknown, node: Node) => void;
		fitViewOptions?: { minZoom?: number };
		children?: ReactNode;
	}) {
		const [mount] = useState(() => {
			flowMounts.count += 1;
			return flowMounts.count;
		});
		return (
			<div data-testid="react-flow" data-fit-min-zoom={fitViewOptions?.minZoom} data-mount={mount}>
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
		);
	},
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

// Fixture explanations, not Model Lab's real text: the shipped text is checked by ModelLabExplanation.test.ts, and these
// tests pin the panel's behavior. Present for the whole file, so every test sees the same store. Nothing for
// `block.linearAttention` or `op.ffn`: those are the missing-text cases.
const explain = {
	block: {
		fullAttention: {
			overview: "Fixture: a full-attention block, briefly.",
			detail: "Fixture: a full-attention block, in detail.",
			math: "Fixture: scores are $QK^T$.",
		},
	},
	component: { attention: { overview: "Fixture: what attention is in general." } },
	op: {
		input: { overview: "Fixture: the residual stream enters the block." },
		attn: { overview: "Fixture: attention in any template." },
		q: { overview: "Fixture: queries in any family." },
		rope: { overview: "Fixture: RoPE in any family." },
		v: { overview: "Fixture: values in any family." },
	},
	arch: {
		llama: { op: { rope: { overview: "Fixture: RoPE as llama.cpp runs it for llama." } } },
		qwen35: {
			op: {
				attn: { overview: "Fixture: attention in qwen35." },
				q: { overview: "Fixture: queries and gates in qwen35." },
			},
		},
	},
	opByKind: { fullAttention: { attn: { overview: "Fixture: attention in a full-attention block." } } },
	trace: { meanNorm: { overview: "Fixture: what a mean norm says and does not say." } },
};

beforeAll(() => {
	// The layout note is looked up by architecture and has no in-code default, so the shipped one is added here.
	const layoutNote = modelLabEn.pages.modelLab.architecture.layoutNote;
	i18next.addResourceBundle("en", "translation", { pages: { modelLab: { explain, architecture: { layoutNote } } } }, true, true);
	// jsdom does not implement scrollIntoView; a graph click scrolls the opened operation into view.
	Element.prototype.scrollIntoView = vi.fn();
});

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
	hyperparameters: null,
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
		// A dense run keeps no routing, unless a test says otherwise.
		http.get(localApiPath("model-lab/experiments/:experimentId/routing"), () => new HttpResponse(null, { status: 404 })),
		http.get(localApiPath("model-lab/experiments"), () =>
			HttpResponse.json({
				platformSupported: true,
				unsupportedMessage: null,
				activeExperimentId: null,
				maxRetainedExperiments: 20,
				maxPinnedExperiments: 20,
				experiments,
			}),
		),
	);
}

// Where Tab goes next: the following element in document order that takes focus from the keyboard.
function nextTabStop(from: HTMLElement): HTMLElement | undefined {
	const stops = [...document.querySelectorAll<HTMLElement>("button, input, select, textarea, a[href], [tabindex]")].filter(
		(element) => element.tabIndex >= 0 && !element.hasAttribute("disabled"),
	);
	return stops[stops.indexOf(from) + 1];
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

type ArchitectureJump = Extract<ModelLabJump, { tab: "architecture" }>;

// Stands in for the page: it holds what the URL would and changes it through the page's own transition. Each "Next jump"
// applies the next of `jumps` as a jump from another tab does.
function ControlledExplorer({
	initial = { model: gguf },
	jumps = [],
	onJump,
	switchTo,
}: {
	initial?: ModelLabPatch;
	jumps?: ArchitectureJump[];
	onJump?: (to: ModelLabJump) => void;
	// A model "Switch model" alternates with the first one, as the page's model picker would.
	switchTo?: string;
}) {
	const [search, setSearch] = useState(() => applyModelLabPatch({}, initial));
	const [at, setAt] = useState(0);
	return (
		<>
			{switchTo !== undefined && (
				<button
					type="button"
					onClick={() =>
						setSearch((previous) => applyModelLabPatch(previous, { model: previous.model === switchTo ? gguf : switchTo }))
					}
				>
					Switch model
				</button>
			)}
			{jumps.length > 0 && (
				<button
					type="button"
					onClick={() => {
						setAt(at + 1);
						setSearch((previous) => applyModelLabPatch(previous, jumpPatch(jumps[(at + 1) % jumps.length] as ArchitectureJump)));
					}}
				>
					Next jump
				</button>
			)}
			<ArchitectureExplorer
				modelName={search.model ?? null}
				selection={search.block ?? null}
				traceId={search.trace ?? null}
				inside={search}
				arrival={0}
				afterNavigation={(run) => run()}
				level={search.level ?? "overview"}
				onSelect={(block, onShown) => {
					setSearch((previous) => applyModelLabPatch(previous, { block: block ?? undefined }));
					// The page without a router: a change is shown as soon as it is made.
					onShown?.();
				}}
				onDetailChange={(patch, onShown) => {
					setSearch((previous) => applyModelLabPatch(previous, patch));
					onShown?.();
				}}
				onTraceChange={(trace) => setSearch((previous) => applyModelLabPatch(previous, { trace: trace ?? undefined }))}
				onLevelChange={(level) => setSearch((previous) => applyModelLabPatch(previous, { level }))}
				onJump={onJump}
			/>
		</>
	);
}

// Opened from another tab: the jump's model, trace and block, as the page writes them.
function JumpingExplorer({ jumps, onJump }: { jumps: ArchitectureJump[]; onJump?: (to: ModelLabJump) => void }) {
	return <ControlledExplorer initial={jumpPatch(jumps[0] as ArchitectureJump)} jumps={jumps} onJump={onJump} />;
}

async function openModel(withRouter = false): Promise<void> {
	renderWithProviders(<ControlledExplorer />, { withRouter });
	await screen.findByTestId("model-lab-architecture-overview");
}

describe("ArchitectureExplorer", () => {
	it("lays out embedding, the blocks and the output, with the MTP block apart and marked not traceable", async () => {
		stubRoutes(detailed);
		await openModel();

		expect(screen.getByRole("button", { name: "Embedding" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Blocks: 2 in this file" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Output" })).toBeTruthy();
		// Two one-block chips: each is the block itself.
		expect(screen.getByRole("button", { name: "Block 0: Linear attention" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Block 1: Full attention" })).toBeTruthy();
		// The MTP block sits under its own group, open only once that group or the block is selected.
		expect(screen.queryByRole("button", { name: "Block 2: MTP, not traceable" })).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "MTP" }));
		expect(screen.getByRole("button", { name: "Block 2: MTP, not traceable" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-overview-only")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-no-output")).toBeNull();
	});

	it("draws the whole-model graph only once asked, keeps the row below it, and a graph pick selects as the row does", async () => {
		stubRoutes(detailed);
		await openModel();

		const toggle = screen.getByRole("switch", { name: "Show graph" });
		expect((toggle as HTMLInputElement).checked).toBe(false);
		expect(screen.queryByTestId("model-lab-architecture-model-graph")).toBeNull();

		fireEvent.click(toggle);
		expect(screen.getByTestId("model-lab-architecture-model-graph")).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-overview")).toBeTruthy();
		fireEvent.click(screen.getByTestId("react-flow-node-1"));
		expect(screen.getByTestId("model-lab-architecture-block-1").getAttribute("aria-pressed")).toBe("true");
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
			"Reordered from the Hugging Face tensor (heads or rotary pairs regrouped); a quantized type also changes the values",
		);
	});

	it("scrolls a group's tensor rows in their own focusable box and wraps a long name, so a phone's page stays its width", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));

		const rows = within(screen.getByTestId("model-lab-architecture-group-blk.1.attention")).getByRole("region", {
			name: "Attention",
		});
		// The focusable region is the ScrollArea's viewport, the element that scrolls, not the container around it.
		expect(rows.tabIndex).toBe(0);
		expect(rows.hasAttribute("data-scrollarea-viewport")).toBe(true);
		const container = rows.parentElement as HTMLElement;
		expect(container.hasAttribute("tabindex")).toBe(false);
		// A ScrollArea with its own bar, not a native overflow box whose bar a phone hides.
		expect(container.style.getPropertyValue("--table-overflow")).toBe("");
		expect(container.classList.contains("mantine-ScrollArea-root")).toBe(true);
		// Cells keep one line, so "144 MiB" never breaks; the box scrolls instead.
		expect(container.style.getPropertyValue("--table-min-width")).toBe("max-content");

		fireEvent.click(screen.getByRole("button", { name: "attn_q.weight" }));
		const hfName = within(screen.getByTestId("model-lab-architecture-tensor")).getByText(
			"model.layers.1.self_attn.q_proj.weight",
		);
		expect(hfName.style.overflowWrap).toBe("anywhere");
	});

	it("says a block a jump asked for is not in this file, opens no detail, and still opens Embedding", async () => {
		stubRoutes(detailed, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		renderWithProviders(<JumpingExplorer jumps={[{ ...jumpTo(traceId), blockIndex: 9 }]} />);

		expect((await screen.findByTestId("model-lab-architecture-block-missing")).textContent).toContain(
			"Block 9 is not in this file.",
		);
		expect(screen.queryByTestId("model-lab-architecture-detail")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Embedding" }));
		expect(screen.getByTestId("model-lab-architecture-detail")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-block-missing")).toBeNull();
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
		stubRoutes({ ...detailed, architecture: "xe-unregistered-arch", support: "overview", templates: detailed.templates });
		await openModel();

		expect(screen.getByTestId("model-lab-architecture-overview-only").textContent).toContain(
			"No operation graph is shown for the xe-unregistered-arch architecture",
		);
		expect(screen.queryByTestId("model-lab-architecture-no-template")).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		expect(screen.getByTestId("model-lab-architecture-read-badge")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-inferred-badge")).toBeNull();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("shows an unreadable file's read failure and no structure", async () => {
		stubRoutes({ ...detailed, support: "unreadable", readFailure: "truncated", blocks: [], templates: [], nonBlock: [] });
		renderWithProviders(<ControlledExplorer />);

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
			isPinned: false,
			appVersion: null,
			notes: null,
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
		fireEvent.click(screen.getByRole("button", { name: "MTP" }));
		fireEvent.click(screen.getByRole("button", { name: "Block 2: MTP, not traceable" }));
		expect(screen.getByTestId("model-lab-architecture-untraceable")).toBeTruthy();
	});

	it("opens the model, trace and block another tab asked for, keeps a later pick, and links back to the trace", async () => {
		stubRoutes(detailed, [
			{
				id: traceId,
				kind: "Trace",
				status: "Succeeded",
				installedModelName: gguf,
				repoId: "Qwen/Qwen3.5-0.8B",
				revision: "rev",
				prompt: "hello",
				errorMessage: null,
				traceByteCount: 10,
				result: null,
				createdAtUtc: "2026-11-06T00:00:00Z",
				updatedAtUtc: "2026-11-06T00:00:00Z",
				isPinned: false,
				appVersion: null,
				notes: null,
			},
		]);
		server.use(
			http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () =>
				HttpResponse.json({
					records: [{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7], topValues: [1] }],
					truncated: false,
					droppedRecords: 0,
					byteCap: 1000,
				}),
			),
		);
		const onJump = vi.fn();

		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} onJump={onJump} />);

		const traced = await screen.findByRole("button", { name: "Block 1: Full attention, traced" });
		expect(traced.getAttribute("aria-pressed")).toBe("true");
		expect((await screen.findByTestId("model-lab-architecture-trace-summary")).textContent).toContain("Mean norm 2");

		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		expect(screen.getByRole("button", { name: "Block 0: Linear attention" }).getAttribute("aria-pressed")).toBe("true");
		expect(traced.getAttribute("aria-pressed")).toBe("false");

		fireEvent.click(screen.getByRole("button", { name: "Open trace" }));
		expect(onJump).toHaveBeenCalledExactlyOnceWith({ tab: "trace", experimentId: traceId });
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
	metadataPresent: false,
	checkpointHiddenSize: hiddenSize,
	checkpointBlockCount: hiddenSize === null ? null : 2,
	ggufEmbeddingLength: hiddenSize === null ? null : 1024,
	ggufBlockCount: hiddenSize === null ? null : 3,
	steerableBlockCount: hiddenSize === null ? null : 2,
	modelType: hiddenSize === null ? null : "qwen3_5",
	checkpointLayerTypes: null,
	attentionSupported: false,
	attentionRefusalMessage: null,
	attentionRefusal: null,
	traceRefusalMessage: null,
	traceRefusal: null,
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
		expect(screen.getByRole("radiogroup", { name: "Read the architecture from" })).toBeTruthy();
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
		renderWithProviders(<ControlledExplorer />, {
			queryClient: new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } }),
		});
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
		await openBlock("MTP");
		fireEvent.click(screen.getByRole("button", { name: "Block 2: MTP, not traceable" }));

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

describe("ArchitectureExplorer explanations", () => {
	it("switches the block's explanation between overview, detail and math, and keeps the level across blocks", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));

		const detail = within(screen.getByTestId("model-lab-architecture-detail"));
		expect(detail.getByText("Fixture: a full-attention block, briefly.")).toBeTruthy();

		fireEvent.click(screen.getByRole("radio", { name: "Detail" }));
		expect(detail.getByText("Fixture: a full-attention block, in detail.")).toBeTruthy();
		expect(detail.queryByText("Fixture: a full-attention block, briefly.")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Math" }));
		// Rendered through ChatMarkdown, whose math chunk loads lazily.
		await waitFor(() => expect(screen.getByTestId("model-lab-architecture-detail").querySelector(".katex")).not.toBeNull());

		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		expect((screen.getByRole("radio", { name: "Math" }) as HTMLInputElement).checked).toBe(true);
	});

	it("labels every entry with its basis: inferred from llama.cpp or an explanation, and the trace summary as derived", async () => {
		stubRoutes(detailed, [
			{
				id: traceId,
				kind: "Trace",
				status: "Succeeded",
				installedModelName: gguf,
				repoId: "Qwen/Qwen3.5-0.8B",
				revision: "rev",
				prompt: "Why?",
				errorMessage: null,
				traceByteCount: 10,
				result: null,
				createdAtUtc: "2026-11-06T00:00:00Z",
				updatedAtUtc: "2026-11-06T00:00:00Z",
				isPinned: false,
				appVersion: null,
				notes: null,
			},
		]);
		server.use(
			http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () =>
				HttpResponse.json({
					records: [{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7], topValues: [1] }],
					truncated: false,
					droppedRecords: 0,
					byteCap: 1000,
				}),
			),
		);
		await openModel();
		await chooseOption(/Trace to lay over the blocks/, `Why? · Qwen/Qwen3.5-0.8B · ${formatTimestamp("2026-11-06T00:00:00Z")}`);
		fireEvent.click(await screen.findByRole("button", { name: "Block 1: Full attention, traced" }));

		const basisOf = (text: string): string | null | undefined =>
			screen
				.getByText(text)
				.closest("[data-testid='model-lab-explain-entry']")
				?.querySelector("[data-testid='model-lab-explain-basis']")?.textContent;
		// The block's text describes llama.cpp's graph, so it carries the same inferred label as the graph.
		expect(basisOf("Fixture: a full-attention block, briefly.")).toBe(
			"Inferred from llama.cpp (llama.cpp src/models/qwen35.cpp @ b12014)",
		);

		fireEvent.click(screen.getByRole("button", { name: /^Attention/ }));
		expect(basisOf("Fixture: what attention is in general.")).toBe("Explanation, not measured");

		// The summary is computed from the run's records; the text about it is an explanation.
		expect(screen.getByTestId("model-lab-architecture-trace-summary").textContent).toContain("Mean norm 2");
		expect(screen.getByTestId("model-lab-architecture-trace-basis").textContent).toBe("Derived");
		fireEvent.click(within(screen.getByTestId("model-lab-explain-trace")).getByRole("button", { name: "Mean norm" }));
		expect(basisOf("Fixture: what a mean norm says and does not say.")).toBe("Explanation, not measured");
	});

	it("opens a dashed node's explanation from the graph, and a per-kind text before the shared one", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		const operations = within(screen.getByTestId("model-lab-explain-operations"));
		expect(operations.queryByText("Fixture: the residual stream enters the block.")).toBeNull();

		// Only the graph click is under test, not anything that scrolled before it.
		const scroll = vi.mocked(Element.prototype.scrollIntoView);
		scroll.mockClear();

		// `input` holds no tensors (a dashed node), yet clicking it opens its entry.
		fireEvent.click(screen.getByTestId("react-flow-node-input"));
		expect(operations.getByRole("button", { name: "Residual in" }).getAttribute("aria-expanded")).toBe("true");
		expect(operations.getByText("Fixture: the residual stream enters the block.")).toBeTruthy();
		expect(operations.getByTestId("model-lab-explain-basis").textContent).toBe(
			"Inferred from llama.cpp (llama.cpp src/models/qwen35.cpp @ b12014)",
		);

		// The list sits below the graph: the opened item is brought into view, not left below the fold.
		expect(scroll.mock.contexts).toEqual([operations.getByRole("button", { name: "Residual in" }).closest("[data-item-id]")]);

		fireEvent.click(screen.getByTestId("react-flow-node-attn"));
		expect(operations.getByText("Fixture: attention in a full-attention block.")).toBeTruthy();
		// The kind text outranks the architecture's own text too, not only the shared one.
		expect(operations.queryByText("Fixture: attention in qwen35.")).toBeNull();
		expect(operations.queryByText("Fixture: attention in any template.")).toBeNull();
		expect(operations.queryByText("Fixture: the residual stream enters the block.")).toBeNull();
	});

	it("shows a family's own operation text before the shared one, and the shared one where it has none", async () => {
		const fullAttention = detailed.blocks[1] as (typeof detailed.blocks)[number];
		stubRoutes({
			...detailed,
			architecture: "llama",
			blocks: [fullAttention],
			templates: [
				{
					kind: "fullAttention",
					provenance: "inferred",
					source: "llama.cpp src/models/llama.cpp @ b12014",
					nodes: [
						{ id: "rope", label: "RoPE on Q and K", component: "attention" },
						{ id: "v", label: "Value projection · attn_v", component: "attention" },
					],
					edges: [{ from: "v", to: "rope" }],
				},
			],
		});
		await openModel();

		const layoutNote = screen.getByTestId("model-lab-architecture-layout-note");
		expect(layoutNote.textContent).toContain("assume the standard LlamaForCausalLM conversion");
		// The note's module paths wrap on a narrow screen instead of being clipped.
		expect(layoutNote.style.overflowWrap).toBe("anywhere");
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		const operations = within(screen.getByTestId("model-lab-explain-operations"));

		fireEvent.click(screen.getByTestId("react-flow-node-rope"));
		expect(operations.getByText("Fixture: RoPE as llama.cpp runs it for llama.")).toBeTruthy();
		expect(operations.queryByText("Fixture: RoPE in any family.")).toBeNull();

		fireEvent.click(screen.getByTestId("react-flow-node-v"));
		expect(operations.getByText("Fixture: values in any family.")).toBeTruthy();
	});

	it("gives qwen35 its own text for a node and no layout note", async () => {
		const template = detailed.templates[0] as (typeof detailed.templates)[number];
		stubRoutes({
			...detailed,
			templates: [
				{
					...template,
					nodes: [...template.nodes, { id: "q", label: "Query + gate projection · attn_q", component: "attention" }],
				},
			],
		});
		await openModel();

		expect(screen.queryByTestId("model-lab-architecture-layout-note")).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		fireEvent.click(screen.getByTestId("react-flow-node-q"));
		const operations = within(screen.getByTestId("model-lab-explain-operations"));
		expect(operations.getByText("Fixture: queries and gates in qwen35.")).toBeTruthy();
		expect(operations.queryByText("Fixture: queries in any family.")).toBeNull();
	});

	it("says why a described architecture draws no graph when the file's layout has no template", async () => {
		stubRoutes({ ...detailed, architecture: "llama", templates: [] });
		await openModel();

		expect(screen.getByTestId("model-lab-architecture-no-template").textContent).toBe(
			"No operation graph is drawn for this file: its layout, for example mixture-of-experts, is not one the llama template describes. The tensor groups are read from the file as usual.",
		);
		expect(screen.queryByTestId("model-lab-architecture-overview-only")).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		expect(screen.getByTestId("model-lab-architecture-read-badge")).toBeTruthy();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("shows no missing-template notice for a file without blocks, such as a tensorless first shard", async () => {
		stubRoutes({ ...detailed, architecture: "llama", blocks: [], templates: [] });
		await openModel();

		expect(screen.queryByTestId("model-lab-architecture-no-template")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-overview-only")).toBeNull();
	});

	it("reaches and opens every operation through buttons the keyboard moves between", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		const operations = within(screen.getByTestId("model-lab-explain-operations"));
		const controls = operations.getAllByRole("button");
		expect(controls.map((control) => control.textContent)).toEqual(["Residual in", "Gated attention", "SwiGLU FFN"]);

		// Native buttons in the tab order: Enter and Space activate them without any handler of ours.
		const first = controls[0] as HTMLButtonElement;
		expect(first.tagName).toBe("BUTTON");
		expect(first.tabIndex).toBe(0);
		vi.mocked(Element.prototype.scrollIntoView).mockClear();
		first.focus();
		fireEvent.keyDown(first, { key: "ArrowDown" });
		expect(document.activeElement).toBe(controls[1]);
		fireEvent.click(document.activeElement as HTMLElement);
		expect(controls[1]?.getAttribute("aria-expanded")).toBe("true");
		// Opened from the list itself, the item is already where the operator is: nothing scrolls.
		expect(vi.mocked(Element.prototype.scrollIntoView)).not.toHaveBeenCalled();
		expect(operations.getByText("Fixture: attention in a full-attention block.")).toBeTruthy();
	});

	it("shows nothing for an entry no language has text for, never the raw key", async () => {
		stubRoutes(detailed);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		expect(screen.queryByTestId("model-lab-explain-entry")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention" }));
		const operations = within(screen.getByTestId("model-lab-explain-operations"));
		fireEvent.click(operations.getByRole("button", { name: "SwiGLU FFN" }));
		expect(operations.getByRole("button", { name: "SwiGLU FFN" }).getAttribute("aria-expanded")).toBe("true");
		expect(operations.queryByTestId("model-lab-explain-entry")).toBeNull();
		expect(document.body.textContent).not.toContain("pages.modelLab.explain");
	});
});

// An OLMoE-like file: its graph has a router, so a laid-over run may have routing. Otherwise the fixture above.
const olmoe = {
	...detailed,
	architecture: "olmoe",
	templates: detailed.templates.map((template) => ({
		...template,
		nodes: [...template.nodes, { id: "router", label: "Router", component: "ffn" }],
	})),
};
const otherTraceId = "66666666-6666-4666-8666-666666666666";
const routed = (layer: number, experts: number[]) => ({ layer, experts, weights: experts.map(() => 0.25) });

function traceRow(id: string, modelName = gguf) {
	return {
		id,
		kind: "Trace",
		status: "Succeeded",
		installedModelName: modelName,
		repoId: "allenai/OLMoE-1B-7B-0924-Instruct",
		revision: "rev",
		prompt: `prompt of ${id.slice(0, 1)}`,
		errorMessage: null,
		traceByteCount: 10,
		result: null,
		createdAtUtc: "2026-11-23T00:00:00Z",
		updatedAtUtc: "2026-11-23T00:00:00Z",
		isPinned: false,
		appVersion: null,
		notes: null,
	};
}

// One saved run: block 1 traced, and its routing as `routing` answers. Returns how often the routing was asked for.
function stubRun(id: string, routing: () => Response): { requests: number } {
	const seen = { requests: 0 };
	server.use(
		http.get(localApiPath(`model-lab/experiments/${id}/trace`), () =>
			HttpResponse.json({
				records: [{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7], topValues: [1] }],
				truncated: false,
				droppedRecords: 0,
				byteCap: 1000,
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${id}/routing`), () => {
			seen.requests++;
			return routing();
		}),
	);
	return seen;
}

function routingOf(
	steps: { step: number; blocks: ReturnType<typeof routed>[] }[],
	truncated = false,
	layers = [0, 1],
): () => Response {
	return () =>
		HttpResponse.json({
			layers,
			experts: 64,
			expertsPerToken: 2,
			normalizedTopK: false,
			steps,
			truncated,
			droppedSteps: truncated ? 1 : 0,
		});
}

// Run A: block 1 chose experts 5 and 9, then 5 and 12; block 0 chose 40 and 41. Run B, cut after two steps: block 1
// chose 21 and 20, then 21 and 22.
const routingA = routingOf([
	{ step: 0, blocks: [routed(0, [40, 41]), routed(1, [5, 9])] },
	{ step: 1, blocks: [routed(0, [40, 41]), routed(1, [12, 5])] },
]);
const routingB = routingOf(
	[
		{ step: 0, blocks: [routed(0, [30, 31]), routed(1, [21, 20])] },
		{ step: 1, blocks: [routed(0, [30, 31]), routed(1, [21, 22])] },
	],
	true,
);

// Every jump lands on block 1, the one both runs trace.
const jumpTo = (id: string, modelName = gguf): ArchitectureJump => ({
	tab: "architecture",
	modelName,
	traceId: id,
	blockIndex: 1,
});

function usageRows(): (string | null)[] {
	return screen.getAllByTestId("model-lab-architecture-routing-usage-row").map((row) => row.textContent);
}

describe("ArchitectureExplorer routing of a laid-over run", () => {
	it("shows the block's expert usage over the run as derived, and says where it was measured", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);

		const panel = await screen.findByTestId("model-lab-architecture-routing");
		expect(within(panel).getByText("Experts this block chose in the laid-over run")).toBeTruthy();
		expect(within(panel).getByText("Most picked in this run (2 steps)")).toBeTruthy();
		expect(within(panel).getByTestId("model-lab-explain-basis").textContent).toBe("Derived");
		expect(screen.getByTestId("model-lab-architecture-routing-note").textContent).toBe(
			"Read from each block's router as the model ran in PyTorch on the Hugging Face checkpoint. The GGUF in llama.cpp may route differently.",
		);
		// Two experts per token, so the two most picked; ties by expert id.
		expect(usageRows()).toEqual(["E5: picked in 2 of 2 steps", "E9: picked in 1 of 2 steps"]);
		// The residual summary still shows beside it; the page's own Open trace is the way back to the run.
		expect(screen.getByTestId("model-lab-architecture-trace-summary")).toBeTruthy();
		expect(within(panel).queryByRole("button")).toBeNull();
	});

	it("adds nothing for an OLMoE run that kept no routing", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, () => new HttpResponse(null, { status: 404 }));
		const { queryClient } = renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);

		await screen.findByTestId("model-lab-architecture-trace-summary");
		// Only once the routing query has settled on the 404 is the panel's absence a result.
		const key = getModelLabExperimentRoutingQueryKey({ path: { experimentId: traceId } });
		await waitFor(() => expect(queryClient.getQueryState(key)?.status).toBe("error"));
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it("asks an OLMoE run without routing once, however often the operator changes block", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		const routing = stubRun(traceId, () => new HttpResponse(null, { status: 404 }));
		const { queryClient } = renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);
		const key = getModelLabExperimentRoutingQueryKey({ path: { experimentId: traceId } });
		await waitFor(() => expect(queryClient.getQueryState(key)?.status).toBe("error"));

		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention, traced" }));
		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention" }));

		// A remount that refetched would be fetching now; idle means any such request has already been counted.
		await waitFor(() => expect(queryClient.getQueryState(key)?.fetchStatus).toBe("idle"));
		expect(routing.requests).toBe(1);
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it("never asks a Qwen3.5 trace for routing: its graph has no router", async () => {
		stubRoutes(detailed, [traceRow(traceId)]);
		const routing = stubRun(traceId, routingA);
		const { queryClient } = renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);

		// The block detail is up with the trace laid over it, so the routing panel would have mounted by now.
		await screen.findByTestId("model-lab-architecture-trace-summary");
		const state = queryClient.getQueryState(getModelLabExperimentRoutingQueryKey({ path: { experimentId: traceId } }));
		expect(state?.fetchStatus ?? "idle").toBe("idle");
		expect(state?.dataUpdateCount ?? 0).toBe(0);
		expect(routing.requests).toBe(0);
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-routing-missing")).toBeNull();
		expect(screen.getByTestId("model-lab-architecture-block-1").dataset["routed"]).toBe("false");
	});

	it("adds nothing for a block the run's routing does not cover", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingOf([{ step: 0, blocks: [routed(0, [40, 41])] }], false, [0]));
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);

		await screen.findByTestId("model-lab-architecture-trace-summary");
		fireEvent.click(await screen.findByRole("button", { name: "Block 0: Linear attention, routed" }));
		expect(await screen.findByTestId("model-lab-architecture-routing")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention, traced" }));
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it("shows each run's own usage when two runs are laid over the same block in turn", async () => {
		stubRoutes(olmoe, [traceRow(traceId), traceRow(otherTraceId)]);
		stubRun(traceId, routingA);
		stubRun(otherTraceId, routingB);
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId), jumpTo(otherTraceId)]} />);

		await screen.findByTestId("model-lab-architecture-routing");
		expect(usageRows()).toEqual(["E5: picked in 2 of 2 steps", "E9: picked in 1 of 2 steps"]);

		fireEvent.click(screen.getByRole("button", { name: "Next jump" }));

		// The cut-off run counts over its saved steps only.
		expect(await screen.findByText("Most picked in the 2 saved steps")).toBeTruthy();
		expect(usageRows()).toEqual(["E21: picked in 2 of 2 steps", "E20: picked in 1 of 2 steps"]);
	});

	it("applies the same jump again after a later pick, from the run's one fetched routing", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		const routing = stubRun(traceId, routingA);
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);

		await screen.findByTestId("model-lab-architecture-routing");
		fireEvent.click(screen.getByRole("button", { name: "Block 0: Linear attention, routed" }));
		expect(usageRows()).toEqual(["E40: picked in 2 of 2 steps", "E41: picked in 2 of 2 steps"]);

		fireEvent.click(screen.getByRole("button", { name: "Next jump" }));

		expect(screen.getByRole("button", { name: "Block 1: Full attention, traced, routed" }).getAttribute("aria-pressed")).toBe(
			"true",
		);
		expect(usageRows()).toEqual(["E5: picked in 2 of 2 steps", "E9: picked in 1 of 2 steps"]);
		expect(routing.requests).toBe(1);
	});

	it("hides the routing when the operator clears the laid-over trace", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);
		await screen.findByTestId("model-lab-architecture-routing");

		fireEvent.click(screen.getByRole("button", { name: "Clear the laid-over trace" }));

		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-trace-summary")).toBeNull();
	});

	it("reaches the named clear button by Tab, clears the trace from it and puts focus back on the field", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} />);
		await screen.findByTestId("model-lab-architecture-routing");
		const field = screen.getByRole("combobox", { name: /Trace to lay over the blocks/ });
		const clear = screen.getByRole("button", { name: "Clear the laid-over trace" });

		expect(nextTabStop(field)).toBe(clear);
		clear.focus();
		expect(document.activeElement).toBe(clear);
		// Enter or Space on a focused native button fires its click; jsdom does not synthesize that, so the test does.
		fireEvent.click(document.activeElement as HTMLElement);

		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
		expect(screen.queryByRole("button", { name: "Clear the laid-over trace" })).toBeNull();
		expect(document.activeElement).toBe(field);
	});

	it("drops a run deleted since it was laid over once the list refreshes without it", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		const { queryClient } = renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} onJump={vi.fn()} />);
		await screen.findByTestId("model-lab-architecture-routing");
		expect(screen.getByRole("button", { name: "Open trace" })).toBeTruthy();

		stubRoutes(olmoe, []);
		await act(() => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }));

		await waitFor(() => expect(screen.queryByRole("button", { name: "Open trace" })).toBeNull());
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-trace-summary")).toBeNull();
	});

	it("keeps a deleted run off the overlay when a later list refresh fails", async () => {
		stubRoutes(olmoe, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		const { queryClient } = renderWithProviders(<JumpingExplorer jumps={[jumpTo(traceId)]} onJump={vi.fn()} />);
		await screen.findByTestId("model-lab-architecture-routing");
		stubRoutes(olmoe, []);
		await act(() => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }));
		await waitFor(() => expect(screen.queryByRole("button", { name: "Open trace" })).toBeNull());

		server.use(http.get(localApiPath("model-lab/experiments"), () => new HttpResponse(null, { status: 500 })));
		await act(() => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }));

		// The failed refetch keeps the list without the run, so the run stays off.
		await waitFor(() => expect(queryClient.getQueryState(listModelLabExperimentsQueryKey())?.status).toBe("error"));
		expect(screen.queryByRole("button", { name: "Open trace" })).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});
});

describe("ArchitectureExplorer jumps between models", () => {
	it("starts the block's detail afresh on a same-block jump to another model", async () => {
		const otherModel = "org/Other-GGUF:Q4_K_M";
		stubRoutes(detailed, [traceRow(traceId), traceRow(otherTraceId, otherModel)]);
		stubRun(traceId, routingA);
		stubRun(otherTraceId, routingB);
		// Other model first and kept cached, so coming back to it shows block 1 in the same render as the jump: only the
		// detail's key can then tell the two models' forms apart.
		renderWithProviders(<JumpingExplorer jumps={[jumpTo(otherTraceId, otherModel), jumpTo(traceId)]} />, {
			queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } }),
		});
		await screen.findByTestId("model-lab-architecture-detail");
		fireEvent.click(screen.getByRole("button", { name: "Next jump" }));

		fireEvent.click(await screen.findByRole("radio", { name: "Hugging Face config.json" }));
		fireEvent.change(screen.getByRole("textbox", { name: "Repository" }), { target: { value: "org/Typed-For-Tiny" } });
		fireEvent.click(screen.getByRole("button", { name: "Next jump" }));

		// The other model's form starts from its own defaults, not the repository typed for this one.
		await screen.findByRole("radio", { name: "Downloaded checkpoint" });
		expect(screen.queryByRole("textbox", { name: "Repository" })).toBeNull();
		expect((screen.getByRole("radio", { name: "Downloaded checkpoint" }) as HTMLInputElement).checked).toBe(true);
	});

	it("fits the graph afresh for each model it shows, and keeps the view on a block pick", async () => {
		stubRoutes(hybrid);
		server.use(
			http.get(localApiPath("model-lab/architecture/:modelName"), ({ params }) =>
				HttpResponse.json({ ...hybrid, modelName: params["modelName"] }),
			),
		);
		// Kept cached, so coming back to the first model shows its graph in the same render as the switch.
		renderWithProviders(<ControlledExplorer switchTo="org/Other-GGUF:Q4_K_M" />, {
			queryClient: new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } }),
		});
		await screen.findByTestId("model-lab-architecture-overview");
		fireEvent.click(screen.getByTestId("model-lab-architecture-graph-switch"));
		const mount = () => screen.getByTestId("react-flow").getAttribute("data-mount");
		const first = mount();

		fireEvent.click(screen.getByTestId("react-flow-node-4-7"));
		expect(mount()).toBe(first);

		fireEvent.click(screen.getByRole("button", { name: "Switch model" }));
		await waitFor(() => expect(mount()).not.toBe(first));
		const other = mount();
		fireEvent.click(screen.getByRole("button", { name: "Switch model" }));

		expect(mount()).not.toBe(other);
	});
});

describe("ArchitectureExplorer context from a link", () => {
	const traceData = {
		records: [{ step: 0, layer: 1, kind: "F", norm: 2, percentiles: [0, 1, 5], topIndices: [7], topValues: [1] }],
		truncated: false,
		droppedRecords: 0,
		byteCap: 1000,
	};

	it.each([
		["arrives late and names the run another model's", "late"],
		["fails on its first load", "failed"],
	])("lays no linked trace over the blocks, asks for none and offers no Open trace while the list %s", async (_case, list) => {
		stubRoutes(detailed);
		let traceRequests = 0;
		let release: () => void = () => undefined;
		const listed = new Promise<void>((resolve) => {
			release = resolve;
		});
		server.use(
			http.get(localApiPath("model-lab/experiments"), async () => {
				if (list === "failed") {
					return new HttpResponse(null, { status: 500 });
				}
				await listed;
				return HttpResponse.json({
					platformSupported: true,
					unsupportedMessage: null,
					activeExperimentId: null,
					maxRetainedExperiments: 20,
					maxPinnedExperiments: 20,
					experiments: [traceRow(traceId, "org/Other-GGUF:Q4_K_M")],
				});
			}),
			http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () => {
				traceRequests++;
				return HttpResponse.json(traceData);
			}),
		);
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } });
		// Another panel already read this run, so a raw id would draw it from the cache without asking.
		queryClient.setQueryData(getModelLabExperimentTraceQueryKey({ path: { experimentId: traceId } }), traceData);
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 1 }} onJump={vi.fn()} />, {
			queryClient,
		});

		const expectNoOverlay = (): void => {
			expect(screen.getByRole("button", { name: "Block 1: Full attention" }).getAttribute("aria-pressed")).toBe("true");
			expect(screen.queryByTestId("model-lab-architecture-traced-blocks")).toBeNull();
			expect(screen.queryByTestId("model-lab-architecture-trace-summary")).toBeNull();
			expect(screen.queryByRole("button", { name: "Open trace" })).toBeNull();
			expect(traceRequests).toBe(0);
		};
		await screen.findByTestId("model-lab-architecture-detail");
		const listKey = listModelLabExperimentsQueryKey();
		if (list === "failed") {
			await waitFor(() => expect(queryClient.getQueryState(listKey)?.status).toBe("error"));
		} else {
			expectNoOverlay();
			release();
			await waitFor(() => expect(queryClient.getQueryState(listKey)?.status).toBe("success"));
		}

		expectNoOverlay();
	});

	it("says a linked Output the file does not hold is not in it, and opens no detail", async () => {
		stubRoutes({ ...detailed, nonBlock: detailed.nonBlock.filter((group) => group.componentId === "embedding") });
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, block: "output" }} />);

		expect((await screen.findByTestId("model-lab-architecture-block-missing")).textContent).toBe(
			"Output is not in this file. One possible reason: the file is one part of a split GGUF and it is in another part.",
		);
		expect(screen.queryByTestId("model-lab-architecture-detail")).toBeNull();
	});

	it.each([
		[
			"output",
			["embedding"],
			"Die Ausgabe ist nicht in dieser Datei. Ein möglicher Grund: Die Datei ist ein Teil eines aufgeteilten GGUF, und die Ausgabe liegt in einem anderen Teil.",
		],
		[
			"embedding",
			["output_norm", "output"],
			"Das Embedding ist nicht in dieser Datei. Ein möglicher Grund: Die Datei ist ein Teil eines aufgeteilten GGUF, und das Embedding liegt in einem anderen Teil.",
		],
	] as const)(
		"says in German, with its article, that a linked %s the file does not hold is not in it",
		async (block, kept, text) => {
			stubRoutes({ ...detailed, nonBlock: detailed.nonBlock.filter((group) => kept.some((id) => id === group.componentId)) });
			renderWithProviders(<ControlledExplorer initial={{ model: gguf, block }} />);
			await screen.findByTestId("model-lab-architecture-block-missing");

			try {
				await loadModelLabStrings("de");
				await act(() => i18next.changeLanguage("de"));
				expect(screen.getByTestId("model-lab-architecture-block-missing").textContent).toBe(text);
			} finally {
				await act(() => i18next.changeLanguage("en"));
			}
		},
	);

	it("opens the linked block at the linked explanation level", async () => {
		stubRoutes(detailed);
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, block: 1, level: "detail" }} />);

		expect(await screen.findByText("Fixture: a full-attention block, in detail.")).toBeTruthy();
		expect((screen.getByRole("radio", { name: "Detail" }) as HTMLInputElement).checked).toBe(true);
	});
});

// A hybrid file: 3 linear + 1 full attention, twice, then one MTP block; an Other group outside the blocks.
const hybridBlock = (index: number, kind: string) => {
	const component = kind === "linearAttention" ? "ssm" : kind === "mtp" ? "mtp" : "attention";
	const name = { ssm: "ssm_out", mtp: "nextn.eh_proj", attention: "attn_q" }[component];
	return {
		index,
		kind,
		byteSize: 1,
		groups: [
			{
				componentId: `blk.${index}.${component}`,
				component,
				provenance: "read",
				tensors: [tensor(`blk.${index}.${name}.weight`, null, null)],
			},
		],
	};
};
const header = {
	fileType: "Q4_K_M",
	declaredParamCount: 752_393_024,
	blockCount: 9,
	nextnPredictLayers: 1,
	splitCount: null,
	attentionHeadCount: 8,
	attentionHeadCountKv: 2,
	attentionKeyLength: 256,
	attentionValueLength: 256,
	embeddingLength: 1024,
	contextLength: 262_144,
	expertCount: 0,
	expertUsedCount: null,
	fullAttentionInterval: 4,
};
const hybrid = {
	...detailed,
	hyperparameters: header,
	blocks: [
		...[..."LLLFLLLF"].map((letter, index) => hybridBlock(index, letter === "L" ? "linearAttention" : "fullAttention")),
		hybridBlock(8, "mtp"),
	],
	nonBlock: [
		...detailed.nonBlock,
		{ componentId: "other", component: "other", provenance: "read", tensors: [tensor("rope_freqs.weight", null, null)] },
	],
};

function breadcrumb(): string[] {
	const nav = screen.getByTestId("model-lab-architecture-breadcrumb");
	return [...nav.querySelectorAll("button, [aria-current]")].map((item) =>
		item.hasAttribute("aria-current") ? `[${item.textContent}]` : (item.textContent ?? ""),
	);
}

function valueText(key: string): string | null | undefined {
	return screen.queryByTestId(`model-lab-architecture-value-${key}`)?.textContent;
}

describe("ArchitectureExplorer hierarchy", () => {
	it("opens on the whole model: the header's values with their basis, what lies outside the blocks, and the counts", async () => {
		stubRoutes(hybrid);
		await openModel();

		expect(breadcrumb()).toEqual(["[Model]"]);
		const root = within(screen.getByTestId("model-lab-architecture-root"));
		expect(root.getByRole("heading", { name: "Whole model" })).toBeTruthy();
		expect(valueText("fileType")).toBe("QuantizationQ4_K_MRead from the file");
		expect(valueText("declaredParamCount")).toBe("Parameters752,393,024Read from the file");
		expect(valueText("blockCount")).toBe("Layers9Read from the file");
		expect(valueText("nextnPredictLayers")).toBe("MTP layers1Read from the file");
		expect(valueText("blocksInFile")).toBe("Blocks in this file8Derived");
		expect(valueText("mtpInFile")).toBe("MTP blocks in this file1Derived");
		expect(valueText("contextLength")).toBe("Context length262,144Read from the file");
		expect(valueText("embeddingLength")).toBe("Width1,024Read from the file");
		expect(screen.getByTestId("model-lab-architecture-root-outside").textContent).toBe(
			"Outside the blocks: Embedding, Output, Other.",
		);
		expect(screen.queryByTestId("model-lab-architecture-split")).toBeNull();
		// The row: the groups outside, the blocks, MTP and Other, with the chips below and no block open.
		expect(screen.getByRole("button", { name: "Blocks: 8 in this file" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Other" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "0–3 · 3 linear + 1 full" }).getAttribute("aria-pressed")).toBe("false");
		expect(screen.getByRole("button", { name: "4–7 · 3 linear + 1 full" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-block-0")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-gap")).toBeNull();
	});

	it("opens a chip's blocks in the row and its detail: kinds, layout, types and operations as text, no graph", async () => {
		stubRoutes(hybrid);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "0–3 · 3 linear + 1 full" }));

		expect(screen.getByRole("button", { name: "0–3 · 3 linear + 1 full" }).getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByRole("button", { name: "Block 3: Full attention" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-block-4")).toBeNull();
		expect(breadcrumb()).toEqual(["Model", "Blocks", "[0–3]"]);
		const range = within(screen.getByTestId("model-lab-architecture-range"));
		expect(range.getByRole("heading", { name: "Blocks 0–3" })).toBeTruthy();
		expect(range.getByTestId("model-lab-architecture-range-kinds").textContent).toBe(
			"4 blocks: Linear attention ×3 + Full attention ×1",
		);
		expect(range.getByTestId("model-lab-architecture-range-layout").textContent).toBe(
			"Blocks of one kind hold the same tensors: component, name and shape.",
		);
		expect(range.getByTestId("model-lab-architecture-range-types").textContent).toBe(
			"Corresponding tensors use the same stored types.",
		);
		// Only the full-attention kind has a template here; the linear kind gets no invented text.
		const operations = within(range.getByTestId("model-lab-architecture-range-operations-fullAttention"));
		expect(operations.getAllByRole("button").map((button) => button.textContent)).toEqual([
			"Residual in",
			"Gated attention",
			"SwiGLU FFN",
		]);
		expect(range.queryByTestId("model-lab-architecture-range-operations-linearAttention")).toBeNull();
		const heading = range.getByText("Operations of a Full attention block").parentElement as HTMLElement;
		expect(within(heading).getByTestId("model-lab-explain-basis").textContent).toBe(
			"Inferred from llama.cpp (llama.cpp src/models/qwen35.cpp @ b12014)",
		);
		// The layout and type lines are computed here from what the file holds.
		expect(
			within(range.getByTestId("model-lab-architecture-range-layout").closest(".mantine-Group-root") as HTMLElement).getByTestId(
				"model-lab-explain-basis",
			).textContent,
		).toBe("Derived");
		expect(valueText("attentionHeadCount")).toBe("Attention heads8Read from the file");
		expect(screen.queryByTestId("react-flow")).toBeNull();
		expect(screen.queryByTestId("model-lab-architecture-read-badge")).toBeNull();
		expect(
			within(range.getByTestId("model-lab-architecture-open-blocks"))
				.getAllByRole("button")
				.map((button) => button.textContent),
		).toEqual(["Open block 0", "Open block 1", "Open block 2", "Open block 3"]);
	});

	it.each([
		[
			"a chip of one linear and one full block",
			[
				hybridBlock(0, "linearAttention"),
				hybridBlock(1, "fullAttention"),
				hybridBlock(2, "linearAttention"),
				hybridBlock(3, "fullAttention"),
			],
			"Nothing in this group to compare: no two blocks are of one kind.",
		],
		[
			"a chip whose blocks of one kind share no tensor",
			[
				hybridBlock(0, "fullAttention"),
				{
					...hybridBlock(1, "fullAttention"),
					groups: [{ ...hybridBlock(1, "fullAttention").groups[0], tensors: [tensor("blk.1.attn_k.weight", null, null)] }],
				},
			],
			"The tensors differ from the first block of the same kind at blocks 1.",
		],
	])("claims no shared layout or stored types for %s", async (_case, blocks, layout) => {
		stubRoutes({ ...hybrid, hyperparameters: { ...header, blockCount: blocks.length, nextnPredictLayers: null }, blocks });
		await openModel();

		fireEvent.click(screen.getByTestId("model-lab-architecture-chip-0-1"));

		const range = within(await screen.findByTestId("model-lab-architecture-range"));
		expect(range.getByTestId("model-lab-architecture-range-layout").textContent).toBe(layout);
		expect(range.getByTestId("model-lab-architecture-range-types").textContent).toBe(
			"Nothing in this group to compare: no tensors correspond between blocks of one kind.",
		);
	});

	it("moves focus to the new heading when a graph node selects it, since a node takes no focus", async () => {
		stubRoutes(hybrid);
		await openModel();
		const graphSwitch = screen.getByTestId("model-lab-architecture-graph-switch");
		fireEvent.click(graphSwitch);
		graphSwitch.focus();

		fireEvent.click(await screen.findByTestId("react-flow-node-4-7"));

		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Blocks 4–7" }));
	});

	it("moves focus to each new heading when a detail control or the breadcrumb opens it, and not on a row pick", async () => {
		stubRoutes(hybrid);
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "4–7 · 3 linear + 1 full" }));
		expect(document.activeElement).toBe(document.body);

		fireEvent.click(screen.getByRole("button", { name: "Open block 7" }));
		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Block 7: Full attention" }));
		expect(breadcrumb()).toEqual(["Model", "Blocks", "4–7", "[Block 7]"]);
		expect(screen.getByRole("button", { name: "Block 7: Full attention" }).getAttribute("aria-pressed")).toBe("true");

		fireEvent.click(within(screen.getByTestId("model-lab-architecture-breadcrumb")).getByRole("button", { name: "4–7" }));
		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Blocks 4–7" }));

		fireEvent.click(within(screen.getByTestId("model-lab-architecture-breadcrumb")).getByRole("button", { name: "Blocks" }));
		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Blocks in this file" }));

		fireEvent.click(within(screen.getByTestId("model-lab-architecture-breadcrumb")).getByRole("button", { name: "Model" }));
		expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Whole model" }));

		// A row pick leaves focus where it is: the row stays the keyboard path.
		const row = screen.getByRole("button", { name: "Output" });
		row.focus();
		fireEvent.click(row);
		expect(document.activeElement).toBe(row);
	});

	it("shows every block's counts per kind, the repeating pattern, the attention values and the chips as buttons", async () => {
		stubRoutes(hybrid);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Blocks: 8 in this file" }));

		expect(breadcrumb()).toEqual(["Model", "[Blocks]"]);
		const stack = within(screen.getByTestId("model-lab-architecture-stack"));
		expect(stack.getByTestId("model-lab-architecture-kind-counts").textContent).toBe("Linear attention: 6 · Full attention: 2");
		expect(stack.getByTestId("model-lab-architecture-pattern").textContent).toBe(
			"3 × Linear attention + 1 × Full attention, 2 times",
		);
		expect(valueText("attentionHeadCountKv")).toBe("Key/value heads2Read from the file");
		expect(valueText("attentionKeyLength")).toBe("Key head size256Read from the file");
		expect(valueText("fullAttentionInterval")).toBe("Full-attention interval4Read from the file");
		// Zero experts: a dense file, so neither expert value is shown.
		expect(valueText("expertCount")).toBeUndefined();
		expect(valueText("expertUsedCount")).toBeUndefined();

		fireEvent.click(stack.getByRole("button", { name: "4–7 · 3 linear + 1 full" }));
		expect(screen.getByRole("heading", { name: "Blocks 4–7" })).toBe(document.activeElement);
	});

	it("keeps MTP apart: its own detail with the separate-path note, and its block under it", async () => {
		stubRoutes(hybrid);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "MTP" }));

		expect(breadcrumb()).toEqual(["Model", "[MTP]"]);
		expect(screen.getByTestId("model-lab-architecture-mtp-note").textContent).toBe(
			"These blocks predict further tokens on a separate path beside the main stack; they are not one of its blocks.",
		);
		fireEvent.click(screen.getByRole("button", { name: "Open block 8" }));
		expect(breadcrumb()).toEqual(["Model", "MTP", "[Block 8]"]);
		expect(screen.getByRole("button", { name: "Block 8: MTP, not traceable" }).getAttribute("aria-pressed")).toBe("true");
	});

	it("opens Other and Embedding each with only their own groups, and the embedding's vocabulary and width", async () => {
		stubRoutes(hybrid);
		await openModel();

		fireEvent.click(screen.getByRole("button", { name: "Other" }));
		expect(breadcrumb()).toEqual(["Model", "[Other]"]);
		expect(screen.getByTestId("model-lab-architecture-group-other")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-group-embedding")).toBeNull();

		fireEvent.click(screen.getByRole("button", { name: "Embedding" }));
		expect(breadcrumb()).toEqual(["Model", "[Embedding]"]);
		expect(screen.queryByTestId("model-lab-architecture-group-other")).toBeNull();
		// token_embd.weight is 1024 × 256 in GGUF order: 256 tokens, derived from the shape.
		expect(valueText("vocabulary")).toBe("Vocabulary256Derived");
		expect(valueText("embeddingLength")).toBe("Width1,024Read from the file");
	});

	it("shows a linked block inside its chip without a click, with the chip in the breadcrumb", async () => {
		stubRoutes(hybrid);
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, block: 5 }} />);

		const linked = await screen.findByRole("button", { name: "Block 5: Linear attention" });
		expect(linked.getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByRole("button", { name: "4–7 · 3 linear + 1 full" }).getAttribute("aria-pressed")).toBe("false");
		expect(screen.queryByTestId("model-lab-architecture-block-0")).toBeNull();
		expect(breadcrumb()).toEqual(["Model", "Blocks", "4–7", "[Block 5]"]);
		// A linked block is not a pick made from a detail: focus stays.
		expect(document.activeElement).toBe(document.body);
	});

	it.each([
		["1-2", { ...hybrid }, "Blocks 1–2 are not one group in this file. Choose a group from the blocks above."],
		["stack", { ...hybrid, blocks: [] }, "No blocks of the main stack are in this file."],
		["mtp", { ...detailed, blocks: detailed.blocks.slice(0, 2) }, "No multi-token prediction blocks are in this file."],
		["other", detailed, "No other tensors outside the blocks are in this file."],
	] as const)(
		"says a linked %s the file does not hold as one is not in it, and guesses nothing",
		async (block, descriptor, text) => {
			stubRoutes(descriptor);
			renderWithProviders(<ControlledExplorer initial={{ model: gguf, block }} />);

			expect((await screen.findByTestId("model-lab-architecture-block-missing")).textContent).toContain(text);
			expect(screen.queryByTestId("model-lab-architecture-range")).toBeNull();
			expect(screen.queryByTestId("model-lab-architecture-stack")).toBeNull();
			expect(screen.queryByTestId("model-lab-architecture-mtp")).toBeNull();
			expect(screen.queryByTestId("model-lab-architecture-detail")).toBeNull();
		},
	);

	it("says a file holding only MTP blocks has no main-stack blocks, while it lists the MTP block", async () => {
		stubRoutes({ ...hybrid, blocks: hybrid.blocks.slice(8) });
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, block: "stack" }} />);

		expect((await screen.findByTestId("model-lab-architecture-block-missing")).textContent).toBe(
			"No blocks of the main stack are in this file. One possible reason: the file is one part of a split GGUF and these blocks are in other parts.",
		);
		fireEvent.click(screen.getByRole("button", { name: "MTP" }));
		expect(screen.getByRole("button", { name: "Block 8: MTP, not traceable" })).toBeTruthy();
	});

	it("marks the blocks a shard lacks, and states no pattern for it", async () => {
		// Blocks 4 to 7 of 9: the first four are in another part of the split file.
		stubRoutes({
			...hybrid,
			hyperparameters: { ...header, splitCount: 2 },
			blocks: hybrid.blocks.slice(4, 8),
			nonBlock: [],
		});
		await openModel();

		expect(screen.getAllByTestId("model-lab-architecture-gap").map((gap) => gap.textContent)).toEqual([
			"… blocks not in this file …",
		]);
		expect(screen.getByTestId("model-lab-architecture-split").textContent).toBe(
			"This file is one of the 2 parts of a split GGUF. The blocks counted here are the ones in this file.",
		);
		expect(screen.getByTestId("model-lab-architecture-no-output").textContent).toBe(
			"No separate output tensor in this file; the output may share the embedding weights, or it is in another part of the split GGUF.",
		);
		fireEvent.click(screen.getByRole("button", { name: "Blocks: 4 in this file" }));
		expect(screen.queryByTestId("model-lab-architecture-pattern")).toBeNull();
	});
});

describe("ArchitectureExplorer values", () => {
	it("shows no header value a file does not hold, and a zero as zero", async () => {
		stubRoutes({ ...hybrid, hyperparameters: { ...header, fileType: null, declaredParamCount: null, contextLength: 0 } });
		await openModel();

		expect(valueText("fileType")).toBeUndefined();
		expect(valueText("declaredParamCount")).toBeUndefined();
		expect(valueText("contextLength")).toBe("Context length0Read from the file");
	});

	it("shows only what is counted from the file when the response has no header values", async () => {
		stubRoutes({ ...hybrid, hyperparameters: null });
		await openModel();

		expect(
			[
				...screen
					.getByTestId("model-lab-architecture-values")
					.querySelectorAll("[data-testid^='model-lab-architecture-value-text-']"),
			].map((value) => value.getAttribute("data-testid")),
		).toEqual(["model-lab-architecture-value-text-blocksInFile", "model-lab-architecture-value-text-mtpInFile"]);
	});

	it("shows the experts and the experts per token of a mixture-of-experts file", async () => {
		stubRoutes({ ...hybrid, hyperparameters: { ...header, expertCount: 64, expertUsedCount: 8 } });
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Blocks: 8 in this file" }));

		expect(valueText("expertCount")).toBe("Experts64Read from the file");
		expect(valueText("expertUsedCount")).toBe("Experts per token8Read from the file");
	});

	it.each([
		["a rank-1 embedding", [1024]],
		["a rank-3 embedding", [4, 1024, 32]],
	])("shows no vocabulary for %s", async (_case, shape) => {
		stubRoutes({
			...hybrid,
			nonBlock: hybrid.nonBlock.map((group) =>
				group.componentId === "embedding"
					? { ...group, tensors: [{ ...tensor("token_embd.weight", null, null), shape }] }
					: group,
			),
		});
		await openModel();
		fireEvent.click(screen.getByRole("button", { name: "Embedding" }));

		expect(valueText("vocabulary")).toBeUndefined();
		expect(valueText("embeddingLength")).toBe("Width1,024Read from the file");
	});

	it("puts every value's (i) in the Tab order with the value's name", async () => {
		stubRoutes(hybrid);
		await openModel();

		const infos = screen.getAllByTestId(/^model-lab-architecture-value-info-/);
		expect(infos).toHaveLength(8);
		for (const info of infos) {
			expect(info.tagName).toBe("BUTTON");
			expect(info.tabIndex).toBe(0);
			expect(info.getAttribute("aria-label")).toMatch(/^About /);
		}
		expect(nextTabStop(infos[0] as HTMLElement)).toBe(infos[1]);
	});

	it.each([
		[1, "It includes the multi-token prediction layers"],
		[null, null],
	])("says the layers include MTP layers only when the header declares them (%s)", async (nextn, mention) => {
		stubRoutes({ ...hybrid, hyperparameters: { ...header, nextnPredictLayers: nextn } });
		await openModel();

		fireEvent.focus(screen.getByRole("button", { name: "About Layers" }));

		const card = await screen.findByText(/^The layers the header declares for the whole model/);
		if (mention === null) {
			expect(card.textContent).not.toContain("multi-token");
		} else {
			expect(card.textContent).toContain(mention);
		}
	});
});

// Four full-attention blocks in one chip; the run traced block 1, and its routing covers the blocks its steps name.
const olmoeChip = {
	...olmoe,
	blocks: [0, 1, 2, 3].map((index) => hybridBlock(index, "fullAttention")),
};

describe("ArchitectureExplorer routed markers", () => {
	it("marks the routed blocks in the row and counts traced and routed blocks on the chip and in its detail", async () => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		const routing = stubRun(
			traceId,
			routingOf(
				[
					{ step: 0, blocks: [routed(1, [3, 4]), routed(2, [5, 6])] },
					// Block 9 is not in this file: never marked.
					{ step: 1, blocks: [routed(2, [5, 7]), routed(9, [1, 2])] },
				],
				false,
				[1, 2, 9],
			),
		);
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: "0-3" }} />);

		const chip = await screen.findByRole("button", { name: "0–3 · Full attention ×4 · 1 traced · 2 routed" });
		expect(chip.getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByRole("button", { name: "Block 1: Full attention, traced, routed" }).dataset["routed"]).toBe("true");
		expect(screen.getByRole("button", { name: "Block 2: Full attention, routed" })).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-block-0").dataset["routed"]).toBe("false");
		expect(screen.getByText("A square marks a block whose routing the run recorded.")).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-range-traced").textContent).toBe("Traced in the laid-over run: 1");
		expect(screen.getByTestId("model-lab-architecture-range-routed").textContent).toBe(
			"Routing recorded in the laid-over run: 1–2",
		);

		// The block detail reads the same run's routing from the cache.
		fireEvent.click(screen.getByRole("button", { name: "Open block 2" }));
		expect(await screen.findByTestId("model-lab-architecture-routing")).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention, traced, routed" }));
		// Counted once block 1's detail and its routing panel are up: a remount that asked again would have asked by now.
		await screen.findByRole("heading", { name: "Block 1: Full attention" });
		expect(await screen.findByTestId("model-lab-architecture-routing")).toBeTruthy();
		expect(routing.requests).toBe(1);
	});

	it.each([
		["recorded no steps", routingOf([]), "0–3 · Full attention ×4 · 1 traced · 0 routed"],
		[
			"was cut after its saved steps",
			routingOf([{ step: 0, blocks: [routed(3, [1, 2])] }], true),
			"0–3 · Full attention ×4 · 1 traced · 1 routed",
		],
	])("counts only saved steps when a run %s", async (_case, answer, name) => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		stubRun(traceId, answer);
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId }} />);

		expect(await screen.findByRole("button", { name })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-routing-missing")).toBeNull();
	});

	it("says a run recorded no routing instead of showing zero, and asks once", async () => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		const routing = stubRun(traceId, () => new HttpResponse(null, { status: 404 }));
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: "0-3" }} />);

		expect((await screen.findByTestId("model-lab-architecture-routing-missing")).textContent).toBe(
			"The laid-over run recorded no routing.",
		);
		expect(screen.queryByTestId("model-lab-architecture-routing-failed")).toBeNull();
		expect(screen.getByRole("button", { name: "0–3 · Full attention ×4 · 1 traced" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-range-routed")).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Block 1: Full attention, traced" }));
		fireEvent.click(screen.getByRole("button", { name: "Block 2: Full attention" }));
		// Counted once the last opened detail is up: a remount that asked again would have asked by now.
		await screen.findByRole("heading", { name: "Block 2: Full attention" });
		expect(routing.requests).toBe(1);
	});

	it("says a run's routing could not be read on a failure other than 404, and marks nothing", async () => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		stubRun(traceId, () => new HttpResponse(null, { status: 500 }));
		renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 1 }} />);

		expect((await screen.findByTestId("model-lab-architecture-routing-failed")).textContent).toBe(
			"The run's routing could not be read.",
		);
		expect(screen.queryByTestId("model-lab-architecture-routing-missing")).toBeNull();
		expect(screen.getByTestId("model-lab-architecture-block-1").dataset["routed"]).toBe("false");
		expect(screen.queryByText("A square marks a block whose routing the run recorded.")).toBeNull();
		// The block detail is up with the trace laid over it, so a routing panel would have shown by now.
		expect(await screen.findByTestId("model-lab-architecture-trace-summary")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it("drops every routed marker and the block's routing when a refetch fails, though the earlier answer is cached", async () => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		const answer = { failing: false };
		stubRun(traceId, () => (answer.failing ? new HttpResponse(null, { status: 500 }) : routingA()));
		const { queryClient } = renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 1 }} />);
		expect(await screen.findByTestId("model-lab-architecture-routing")).toBeTruthy();

		answer.failing = true;
		const key = getModelLabExperimentRoutingQueryKey({ path: { experimentId: traceId } });
		await act(() => queryClient.invalidateQueries({ queryKey: key }));

		expect(await screen.findByTestId("model-lab-architecture-routing-failed")).toBeTruthy();
		expect(queryClient.getQueryState(key)?.data).toBeDefined();
		expect(screen.getByTestId("model-lab-architecture-block-1").dataset["routed"]).toBe("false");
		expect(screen.getByRole("button", { name: "Block 1: Full attention, traced" })).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it.each([
		["recorded no steps", routingOf([], false, [0, 1])],
		["kept only steps naming other blocks", routingOf([{ step: 0, blocks: [routed(1, [3, 4])] }], true, [0, 1])],
	])("shows no routing in the detail of a block the run's layers name but its saved steps do not (%s)", async (_case, answer) => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		stubRun(traceId, answer);
		const { queryClient } = renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 0 }} />);

		await screen.findByRole("heading", { name: "Block 0: Full attention" });
		const key = getModelLabExperimentRoutingQueryKey({ path: { experimentId: traceId } });
		await waitFor(() => expect(queryClient.getQueryState(key)?.status).toBe("success"));
		expect(screen.getByTestId("model-lab-architecture-block-0").dataset["routed"]).toBe("false");
		expect(screen.queryByTestId("model-lab-architecture-routing")).toBeNull();
	});

	it.each([
		["cleared", "clear"],
		["deleted since", "delete"],
	])("drops the markers of a run that was %s", async (_case, how) => {
		stubRoutes(olmoeChip, [traceRow(traceId)]);
		stubRun(traceId, routingA);
		const { queryClient } = renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 1 }} />);
		expect(await screen.findByRole("button", { name: "Block 1: Full attention, traced, routed" })).toBeTruthy();

		if (how === "clear") {
			fireEvent.click(screen.getByRole("button", { name: "Clear the laid-over trace" }));
		} else {
			stubRoutes(olmoeChip, []);
			await act(() => queryClient.invalidateQueries({ queryKey: listModelLabExperimentsQueryKey() }));
		}

		expect(await screen.findByRole("button", { name: "Block 1: Full attention" })).toBeTruthy();
		expect(screen.getByTestId("model-lab-architecture-block-0").dataset["routed"]).toBe("false");
		expect(screen.queryByText("A square marks a block whose routing the run recorded.")).toBeNull();
	});

	it("asks no routing for a linked run of another model and marks nothing", async () => {
		stubRoutes(olmoeChip, [traceRow(traceId, "org/Other-GGUF:Q4_K_M")]);
		const routing = stubRun(traceId, routingA);
		const { queryClient } = renderWithProviders(<ControlledExplorer initial={{ model: gguf, trace: traceId, block: 1 }} />);

		await waitFor(() => expect(queryClient.getQueryState(listModelLabExperimentsQueryKey())?.status).toBe("success"));
		expect(await screen.findByRole("button", { name: "Block 1: Full attention" })).toBeTruthy();
		expect(routing.requests).toBe(0);
		expect(screen.queryByTestId("model-lab-architecture-routing-missing")).toBeNull();
	});
});

// The node passes a header value only within 0..2^53-1 (larger ones as null); the generated validator agrees at the edge.
describe("architecture response validator", () => {
	const values = {
		fileType: "Q4_K_M",
		declaredParamCount: Number.MAX_SAFE_INTEGER,
		blockCount: 25,
		nextnPredictLayers: 1,
		splitCount: null,
		attentionHeadCount: 8,
		attentionHeadCountKv: 2,
		attentionKeyLength: 256,
		attentionValueLength: 256,
		embeddingLength: 1024,
		contextLength: 262144,
		expertCount: 0,
		expertUsedCount: null,
		fullAttentionInterval: 4,
	};

	it("accepts the largest safe integer, zero and null values", () => {
		expect(zGetModelLabArchitectureResponse.safeParse({ ...detailed, hyperparameters: values }).success).toBe(true);
		expect(zGetModelLabArchitectureResponse.safeParse({ ...detailed, hyperparameters: null }).success).toBe(true);
	});

	it("rejects a value past the safe integer range", () => {
		const unsafe = { ...values, declaredParamCount: Number.MAX_SAFE_INTEGER + 1 };
		expect(zGetModelLabArchitectureResponse.safeParse({ ...detailed, hyperparameters: unsafe }).success).toBe(false);
	});
});
