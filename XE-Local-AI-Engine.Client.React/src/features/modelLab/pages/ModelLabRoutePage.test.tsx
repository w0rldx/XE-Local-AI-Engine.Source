// @vitest-environment jsdom

// The page behind its real route: the route's search schema and adapter on a memory history, with the panels against
// stubbed routes (MSW). It pins what only the router shows: which writes push and which replace, what Back and Forward
// restore, and that the URL is read and written in one shape.

import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from "@tanstack/react-router";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { modelLabSearchSchema } from "@/features/modelLab/models/ModelLabNavigation";
import playgroundFixture from "@/features/modelLab/models/playgroundTrace.fixture.json";
import { ModelLabRoutePage } from "@/features/modelLab/pages/ModelLabRoutePage";
import { acceptConfirm, openConfirm } from "@/test/ConfirmFlow";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn(), stream: vi.fn() },
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(() => () => undefined),
		onReconnecting: vi.fn(),
		onClosed: vi.fn(),
		release: vi.fn(),
	}),
}));
// React Flow paints nothing into jsdom, so the mock renders each node as a button wired to the one click the page uses.
vi.mock("@xyflow/react", () => ({
	Background: () => null,
	Controls: () => null,
	ReactFlow: ({
		nodes,
		onNodeClick,
	}: {
		nodes: { id: string; data: { label: string } }[];
		onNodeClick?: (event: unknown, node: unknown) => void;
	}) => (
		<div>
			{nodes.map((node) => (
				<button
					key={node.id}
					type="button"
					data-testid={`react-flow-node-${node.id}`}
					onClick={(event) => onNodeClick?.(event, node)}
				>
					{node.data.label}
				</button>
			))}
		</div>
	),
}));
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({ CodeEditor: () => null }));
// Unchanged by the page's URL: their own tests cover them.
vi.mock("@/features/modelLab/components/SamePromptComparison", () => ({ SamePromptComparison: () => null }));
vi.mock("@/features/modelLab/components/TraceCheckpointForm", () => ({ TraceCheckpointForm: () => null }));

setupMswServer();

const gguf = "org/Tiny-GGUF:Q4_K_M";
const otherModel = "org/Other-GGUF:Q8_0";
const traceId = "11111111-1111-4111-8111-111111111111";
const otherTraceId = "22222222-2222-4222-8222-222222222222";
const otherPrompt = "Why is the sky blue?";

const tensor = (ggufName: string) => ({
	ggufName,
	hfName: null,
	shape: [1024],
	ggmlType: "F32",
	byteSize: 4096,
	valueRelation: null,
});

const descriptor = {
	descriptorVersion: 1,
	modelName: gguf,
	architecture: "llama",
	support: "overview",
	readFailure: null,
	suggestedBaseRepo: null,
	hyperparameters: null,
	blocks: [0, 1, 2].map((index) => ({
		index,
		kind: "fullAttention",
		byteSize: 1,
		groups: [
			{
				componentId: `blk.${index}.attention`,
				component: "attention",
				provenance: "read",
				tensors: [tensor(`blk.${index}.attn_q.weight`)],
			},
		],
	})),
	templates: [],
	nonBlock: [
		{ componentId: "embedding", component: "embedding", provenance: "read", tensors: [tensor("token_embd.weight")] },
		{ componentId: "output", component: "output", provenance: "read", tensors: [tensor("output.weight")] },
	],
};

// The same file drawn in detail: each block has an attention and an FFN group, and its template a dashed input node and
// one solid node per group.
const detailedDescriptor = {
	...descriptor,
	support: "detailed",
	blocks: descriptor.blocks.map((block) => ({
		...block,
		groups: [
			{
				componentId: `blk.${block.index}.attention`,
				component: "attention",
				provenance: "read",
				tensors: [tensor(`blk.${block.index}.attn_q.weight`), tensor(`blk.${block.index}.attn_k.weight`)],
			},
			{
				componentId: `blk.${block.index}.ffn`,
				component: "ffn",
				provenance: "read",
				tensors: [tensor(`blk.${block.index}.ffn_down.weight`)],
			},
		],
	})),
	templates: [
		{
			kind: "fullAttention",
			provenance: "inferred",
			source: "llama.cpp src/models/llama.cpp @ b1",
			nodes: [
				{ id: "input", label: "Residual in", component: null },
				{ id: "attn", label: "Self-attention", component: "attention" },
				{ id: "ffn", label: "Feed-forward", component: "ffn" },
			],
			edges: [
				{ from: "input", to: "attn" },
				{ from: "attn", to: "ffn" },
			],
		},
	],
};

const modeling = "models/llama/modeling_llama.py";
const pyModule = (path: string, className: string, classStart: number, classEnd: number) => ({
	path,
	class: className,
	file: modeling,
	classStart,
	classEnd,
	forwardLine: classStart + 1,
});
// A PyTorch index joined to the three blocks: each group has a module, and the block has its own class.
const pyIndex = {
	indexId: "index-1",
	modelName: gguf,
	source: { kind: "huggingface", repoId: "org/Tiny", revision: "rev", baseArtifactId: null },
	index: {
		transformersVersion: "5.17.0",
		torchVersion: "2.13.0",
		modelType: "llama",
		textModelType: "llama",
		modelClass: "LlamaForCausalLM",
		blockPath: "model.layers",
		exportMode: "representative",
		anchoredLines: true,
		blocks: [0, 1, 2].map((index) => ({
			index,
			path: `model.layers.${index}`,
			class: "LlamaDecoderLayer",
			layerType: "full_attention",
			graphId: "g",
			modules: [
				pyModule(`model.layers.${index}`, "LlamaDecoderLayer", 400, 450),
				pyModule(`model.layers.${index}.self_attn`, "LlamaAttention", 210, 280),
				pyModule(`model.layers.${index}.mlp`, "LlamaMLP", 290, 310),
			],
		})),
		outside: [],
		outsideGraphId: null,
		graphs: [{ graphId: "g", nodes: [], edges: [] }],
		exportError: null,
		files: [{ file: modeling, sha256: "abc" }],
	},
	join: {
		blocksJoined: true,
		reason: null,
		ggufTrunkBlockCount: 3,
		pyTorchBlockCount: 3,
		components: [0, 1, 2].flatMap((index) => [
			{ componentId: `blk.${index}.attention`, modulePaths: [`model.layers.${index}.self_attn`] },
			{ componentId: `blk.${index}.ffn`, modulePaths: [`model.layers.${index}.mlp`] },
		]),
		tensors: [],
	},
};

// Every architecture read answers the detailed file; with `withIndex`, the PyTorch index above and its source too.
function stubDetailed(withIndex = false): void {
	server.use(http.get(localApiPath("model-lab/architecture/:modelName"), () => HttpResponse.json(detailedDescriptor)));
	if (withIndex) {
		server.use(
			http.get(localApiPath("model-lab/architecture/:modelName/pytorch"), () => HttpResponse.json(pyIndex)),
			http.get(localApiPath("model-lab/pytorch-source"), () => HttpResponse.json({ file: modeling, sha256: "abc", text: "#" })),
		);
	}
}

const traceRow = {
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
};

const inspection = {
	modelName: gguf,
	readFailure: null,
	ggufVersion: 3,
	architecture: "llama",
	displayName: "Tiny",
	parameterCount: 1000,
	fileType: "Q4_K_M",
	blockCount: 1,
	attentionHeadCount: 16,
	attentionHeadCountKv: 4,
	embeddingLength: 4096,
	contextLength: 4096,
	expertCount: null,
	chatTemplate: null,
	splitCount: null,
	tensorCount: 1,
	totalTensorBytes: 1024,
	headerBytes: 2048,
	fileSizeBytes: 4096,
	blocks: [],
	nonBlockTensors: [],
};

// Run B: the other model's trace, of block 0 only, with no routing.
const otherRow = { ...traceRow, id: otherTraceId, installedModelName: otherModel, prompt: otherPrompt };

const token = { id: 1, text: "Hi", logprob: Math.log(0.6), candidates: [{ id: 1, text: "Hi", logprob: Math.log(0.6) }] };
// Run A also recorded its routing: two full blocks, each choosing 2 of 8 experts.
const routedResult = {
	promptTokens: 1,
	tokens: [token],
	text: "Hi",
	seconds: 1,
	peakVramBytes: 1,
	layerKinds: "FF",
	attnImplementation: "sdpa",
	routing: {
		layers: [0, 1],
		experts: 8,
		expertsPerToken: 2,
		normalizedTopK: false,
		weightEncoding: "bfloat16-bits",
		records: 1,
		truncated: false,
	},
};

const traceOf = (layers: number[]) => ({
	records: layers.map((layer) => ({
		step: 0,
		layer,
		kind: "F",
		norm: layer + 1,
		percentiles: [0, 1, 5],
		topIndices: [7],
		topValues: [1],
	})),
	truncated: false,
	droppedRecords: 0,
	byteCap: 1000,
});

function modelItem(modelName: string) {
	return {
		modelName,
		displayLabel: null,
		isSelected: false,
		kind: "Chat",
		detectedKind: "Chat",
		capabilities: [],
		// Run A's model thinks by default, so a jump's Thinking off shows.
		isReasoningCapable: modelName === gguf,
		isToolCapable: true,
		isOverridden: false,
		provider: "llamacpp",
	};
}

function stubRoutes(): void {
	server.use(
		http.get(localApiPath("models"), () =>
			HttpResponse.json({ isAvailable: true, items: [modelItem(gguf), modelItem(otherModel)] }),
		),
		http.get(localApiPath("model-lab/architecture/:modelName"), () => HttpResponse.json(descriptor)),
		http.get(localApiPath("model-lab/architecture/:modelName/pytorch"), () => new HttpResponse(null, { status: 404 })),
		http.get(localApiPath("models/:modelName/gguf-inspection"), () => HttpResponse.json(inspection)),
		http.get(localApiPath("training/base-artifacts"), () => HttpResponse.json({ items: [] })),
		http.get(localApiPath("model-lab/experiments"), () =>
			HttpResponse.json({
				platformSupported: true,
				unsupportedMessage: null,
				activeExperimentId: null,
				maxRetainedExperiments: 20,
				maxPinnedExperiments: 20,
				experiments: [traceRow, otherRow],
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${traceId}`), () => HttpResponse.json({ ...traceRow, result: routedResult })),
		http.get(localApiPath(`model-lab/experiments/${traceId}/trace`), () => HttpResponse.json(traceOf([0, 1]))),
		http.get(localApiPath(`model-lab/experiments/${traceId}/routing`), () =>
			HttpResponse.json({
				layers: [0, 1],
				experts: 8,
				expertsPerToken: 2,
				normalizedTopK: false,
				steps: [{ step: 0, blocks: [0, 1].map((layer) => ({ layer, experts: [3, 5], weights: [0.5, 0.25] })) }],
				truncated: false,
				droppedSteps: 0,
			}),
		),
		http.get(localApiPath(`model-lab/experiments/${otherTraceId}`), () =>
			HttpResponse.json({ ...otherRow, result: { promptTokens: 1, tokens: [token], text: "Hi", seconds: 1, peakVramBytes: 1 } }),
		),
		http.get(localApiPath(`model-lab/experiments/${otherTraceId}/trace`), () => HttpResponse.json(traceOf([0]))),
	);
}

// The app's shape: a pathless `_layout` with Model Lab under it inside a scrolling element, as the app shell's, and a
// page outside it to start from and go back to.
// `outsideLoaded` holds the outside page's loader open, so a navigation to it stays pending until it settles.
async function renderRoute(entry: string, outsideLoaded?: Promise<void>) {
	const rootRoute = createRootRoute({
		component: () => (
			<ConfirmProvider>
				<div data-testid="page-scroller" style={{ overflowY: "auto" }}>
					<Outlet />
				</div>
			</ConfirmProvider>
		),
	});
	const layoutRoute = createRoute({ getParentRoute: () => rootRoute, id: "_layout", component: Outlet });
	const modelLabRoute = createRoute({
		getParentRoute: () => layoutRoute,
		path: "model-lab",
		validateSearch: modelLabSearchSchema,
		component: ModelLabRoutePage,
	});
	const outsideRoute = createRoute({
		getParentRoute: () => rootRoute,
		path: "outside",
		loader: () => outsideLoaded,
		component: () => <p>Outside</p>,
	});
	const history = createMemoryHistory({ initialEntries: [entry] });
	const router = createRouter({
		routeTree: rootRoute.addChildren([layoutRoute.addChildren([modelLabRoute]), outsideRoute]),
		history,
		scrollRestoration: true,
	});
	const queryClient = createTestQueryClient();
	// The page suspends on its strings' import, and React retries that inside an awaited act scope only.
	await act(async () => {
		// biome-ignore lint/suspicious/noExplicitAny: a locally-built route tree cannot satisfy the app's registered Router type.
		renderWithProviders(<RouterProvider router={router as any} />, { queryClient });
	});
	return { router, history, queryClient };
}

// The URL's search as the router holds it, in a fixed key order.
function searchOf(router: { state: { location: { search: unknown } } }): Record<string, unknown> {
	return Object.fromEntries(Object.entries(router.state.location.search as Record<string, unknown>).sort());
}

async function chooseOption(label: RegExp, option: string): Promise<void> {
	const select = await screen.findByRole("combobox", { name: label });
	fireEvent.click(select);
	const listbox = await waitFor(() => {
		const element = document.getElementById(select.getAttribute("aria-controls") ?? "");
		if (element === null) {
			throw new Error("the dropdown is not open yet");
		}
		return element;
	});
	fireEvent.click(await within(listbox).findByRole("option", { name: option, hidden: true }));
}

function selectedTab(): string | null {
	return screen.getByRole("tab", { selected: true }).textContent;
}

function detailLevelChecked(): boolean {
	const detail = within(screen.getByTestId("model-lab-architecture-detail")).getByRole("radio", { name: "Detail" });
	return (detail as HTMLInputElement).checked;
}

// Block 1 of the detailed file, with what the detail shows inside it.
const blockUrl = (inside = "") => `/model-lab?model=${encodeURIComponent(gguf)}&block=1${inside}`;

// An operation's control in the block's operation list.
function operation(name: string): HTMLElement {
	return within(screen.getByTestId("model-lab-explain-operations")).getByRole("button", { name });
}

function groupSelected(componentId: string): string | undefined {
	return screen.getByTestId(`model-lab-architecture-group-${componentId}`).dataset["selected"];
}

// The source lines the PyTorch panel shows.
function codeLines(): string | null {
	return screen.getByTestId("model-lab-pytorch-file").textContent;
}

// Every render installs a fresh jsdom stub, so this is the one the current page calls.
function scroll() {
	return vi.mocked(Element.prototype.scrollIntoView);
}

describe("Model Lab on its route", () => {
	it("replaces every in-page change, pushes a jump, and Back and Forward walk exactly those entries", async () => {
		stubRoutes();
		const { router, history } = await renderRoute("/outside");
		await screen.findByText("Outside");

		await act(() => router.navigate({ to: "/model-lab" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		const entered = history.length;

		await chooseOption(/Model for Architecture/, gguf);
		await waitFor(() => expect(searchOf(router)).toEqual({ model: gguf }));
		fireEvent.click(await screen.findByRole("button", { name: "0–2 · Full attention ×3" }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: "0-2", model: gguf }));
		expect(history.length).toBe(entered);
		fireEvent.click(await screen.findByRole("button", { name: "Block 1: Full attention" }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, model: gguf }));
		fireEvent.click(within(await screen.findByTestId("model-lab-architecture-detail")).getByRole("radio", { name: "Detail" }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, level: "detail", model: gguf }));
		expect(history.length).toBe(entered);
		await chooseOption(/Trace to lay over the blocks/, `hello · Qwen/Qwen3.5-0.8B · ${formatTimestamp(traceRow.createdAtUtc)}`);
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, level: "detail", model: gguf, trace: traceId }));
		expect(history.length).toBe(entered);

		fireEvent.click(await screen.findByRole("button", { name: "Open trace" }));
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(searchOf(router)).toEqual({
			block: 1,
			experiment: traceId,
			level: "detail",
			model: gguf,
			tab: "trace",
			trace: traceId,
		});
		expect(history.length).toBe(entered + 1);
		await screen.findByTestId("model-lab-trace-view");

		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		await waitFor(() => expect(selectedTab()).toBe("Inspect"));
		expect(searchOf(router)).toEqual({
			block: 1,
			experiment: traceId,
			level: "detail",
			model: gguf,
			tab: "inspect",
			trace: traceId,
		});
		expect(history.length).toBe(entered + 1);

		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ block: 1, level: "detail", model: gguf, trace: traceId });
		expect((await screen.findByRole("button", { name: "Block 1: Full attention, traced" })).getAttribute("aria-pressed")).toBe(
			"true",
		);
		expect(detailLevelChecked()).toBe(true);

		await act(async () => history.back());
		await screen.findByText("Outside");

		await act(async () => history.forward());
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ block: 1, level: "detail", model: gguf, trace: traceId });
		await waitFor(() => expect(detailLevelChecked()).toBe(true));

		await act(async () => history.forward());
		await waitFor(() => expect(selectedTab()).toBe("Inspect"));
		expect(searchOf(router)).toEqual({
			block: 1,
			experiment: traceId,
			level: "detail",
			model: gguf,
			tab: "inspect",
			trace: traceId,
		});
	});

	it.each([
		["block=0", 0],
		["block=3", undefined],
		["block=embedding", "embedding"],
		["block=output", "output"],
	])("reads ?%s and an encoded model name", async (query, block) => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&${query}`);

		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual(block === undefined ? { block: 3, model: gguf } : { block, model: gguf });
		if (block === undefined) {
			// The file has blocks 0 to 2 only.
			expect(await screen.findByTestId("model-lab-architecture-block-missing")).toBeTruthy();
		} else {
			expect(await screen.findByTestId("model-lab-architecture-detail")).toBeTruthy();
		}
	});

	it.each([
		["a negative block", "block=-1"],
		["a fractional block", "block=1.5"],
		["a blank block", "block="],
		["a repeated block", "block=1&block=2"],
		["a malformed trace", `trace=${traceId.slice(1)}`],
		["an unknown level", "level=deep"],
	])("drops %s alone and keeps the rest", async (_case, query) => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&tab=architecture&${query}`);

		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ model: gguf, tab: "architecture" });
		expect(await screen.findByTestId("model-lab-architecture-overview")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-architecture-detail")).toBeNull();
	});

	it("writes a model name the router reads back unchanged, and moves no scroll position", async () => {
		stubRoutes();
		const { router } = await renderRoute("/model-lab");
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => undefined);
		try {
			await chooseOption(/Model for Architecture/, gguf);
			fireEvent.click(await screen.findByRole("button", { name: "0–2 · Full attention ×3" }));
			fireEvent.click(await screen.findByRole("button", { name: "Block 0: Full attention" }));
			fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
			await waitFor(() => expect(searchOf(router)).toEqual({ block: 0, model: gguf, tab: "inspect" }));

			expect(router.state.location.href).toBe(`/model-lab?model=${encodeURIComponent(gguf)}&block=0&tab=inspect`);
			expect(scrollTo).not.toHaveBeenCalled();
		} finally {
			scrollTo.mockRestore();
		}
	});

	it("opens Trace from an Operations link that names no model, with no model notice", async () => {
		stubRoutes();
		await renderRoute("/model-lab?tab=trace");

		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(await screen.findByTestId("model-lab-experiments")).toBeTruthy();
		expect(screen.queryByTestId("model-lab-model-not-installed")).toBeNull();
		expect(screen.queryByTestId("model-lab-no-models")).toBeNull();
	});

	it("prefills Next token from a trace with thinking off, and keeps the prompt out of the URL and history", async () => {
		stubRoutes();
		const { router, history } = await renderRoute(`/model-lab?tab=trace&experiment=${otherTraceId}`);
		await screen.findByTestId("model-lab-trace-view");
		const before = history.length;

		fireEvent.click(await screen.findByTestId("model-lab-trace-try-next-token"));

		await waitFor(() => expect(selectedTab()).toBe("Next token"));
		await waitFor(() => expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe(otherPrompt));
		expect(screen.getByTestId("model-lab-next-token-from-trace")).toBeTruthy();
		expect(searchOf(router)).toEqual({ experiment: otherTraceId, model: otherModel, tab: "nextToken" });
		expect(history.length).toBe(before + 1);
		expect(router.state.location.href).not.toContain("sky");
		expect(JSON.stringify(history.location.state)).not.toContain("sky");
	});

	it("keeps Thinking off from a jump to a model that thinks", async () => {
		stubRoutes();
		await renderRoute(`/model-lab?tab=trace&experiment=${traceId}`);
		await screen.findByTestId("model-lab-trace-view");

		fireEvent.click(await screen.findByTestId("model-lab-trace-try-next-token"));

		await waitFor(() => expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe("hello"));
		expect((screen.getByTestId("direct-inference-thinking-off") as HTMLInputElement).checked).toBe(true);
	});

	it("opens a Next token URL's run in Trace only once Trace is shown", async () => {
		stubRoutes();
		let runRequests = 0;
		server.events.on("request:start", ({ request }) => {
			if (new URL(request.url).pathname.includes(`model-lab/experiments/${traceId}`)) {
				runRequests++;
			}
		});
		try {
			await renderRoute(`/model-lab?tab=nextToken&model=${encodeURIComponent(gguf)}&experiment=${traceId}`);
			await waitFor(() => expect(selectedTab()).toBe("Next token"));
			// The list loads anyway: once it is in, a hidden open run would have asked for its run too.
			await screen.findAllByRole("button", { name: "Qwen/Qwen3.5-0.8B", hidden: true });
			await act(async () => undefined);
			expect(runRequests).toBe(0);
			expect(screen.queryByTestId("model-lab-trace-view")).toBeNull();

			fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));

			expect((await screen.findByTestId("model-lab-trace-view")).textContent).toContain("hello");
			expect(runRequests).toBeGreaterThan(0);
		} finally {
			server.events.removeAllListeners("request:start");
		}
	});

	it("keeps an open run across a switch to another tab and back", async () => {
		stubRoutes();
		await renderRoute(`/model-lab?tab=trace&experiment=${otherTraceId}`);
		await screen.findByTestId("model-lab-trace-view");

		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(screen.getByTestId("model-lab-trace-view").textContent).toContain(otherPrompt);
		fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));

		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(screen.getByTestId("model-lab-trace-view").textContent).toContain(otherPrompt);
	});

	it("leaves the Next token draft alone when the jump's navigation is blocked", async () => {
		stubRoutes();
		const { history } = await renderRoute(`/model-lab?tab=nextToken&model=${encodeURIComponent(gguf)}`);
		await waitFor(() => expect(selectedTab()).toBe("Next token"));
		fireEvent.change(screen.getByLabelText("System prompt (optional)"), { target: { value: "Be brief." } });
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "My draft." } });
		fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		fireEvent.click((await screen.findAllByRole("button", { name: "Qwen/Qwen3.5-0.8B" }))[0] as HTMLElement);
		await screen.findByTestId("model-lab-trace-view");

		const unblock = history.block({ blockerFn: () => true });
		fireEvent.click(await screen.findByTestId("model-lab-trace-try-next-token"));
		await act(async () => undefined);
		expect(selectedTab()).toBe("Trace and steer");
		unblock();
		fireEvent.click(screen.getByRole("tab", { name: "Next token" }));

		await waitFor(() => expect(selectedTab()).toBe("Next token"));
		expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe("My draft.");
		expect((screen.getByLabelText("System prompt (optional)") as HTMLTextAreaElement).value).toBe("Be brief.");
		expect(screen.queryByTestId("model-lab-next-token-from-trace")).toBeNull();
	});

	it("jumps to another model's block 0 from a trace's table, and Back returns to the last model's block", async () => {
		stubRoutes();
		const { router, history } = await renderRoute(
			`/model-lab?tab=trace&model=${encodeURIComponent(gguf)}&block=2&experiment=${otherTraceId}`,
		);
		await screen.findByTestId("model-lab-trace-table");

		fireEvent.click(screen.getByRole("button", { name: "Show block 0 in Architecture" }));

		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ block: 0, experiment: otherTraceId, model: otherModel, trace: otherTraceId });
		expect((await screen.findByRole("button", { name: "Block 0: Full attention, traced" })).getAttribute("aria-pressed")).toBe(
			"true",
		);

		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(searchOf(router)).toEqual({ block: 2, experiment: otherTraceId, model: gguf, tab: "trace" });
	});

	it("jumps to a routed block from the step-through view's routing", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?tab=trace&experiment=${traceId}`);
		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(screen.getByRole("radio", { name: "Step through" }));
		await screen.findByTestId("model-lab-stepper-routing-table");
		fireEvent.click(screen.getByRole("button", { name: /^Block 1, full attention/ }));

		fireEvent.click(
			within(screen.getByTestId("model-lab-stepper-routing")).getByRole("button", { name: "Show block 1 in Architecture" }),
		);

		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ block: 1, experiment: traceId, model: gguf, trace: traceId });
		expect((await screen.findByRole("button", { name: "Block 1: Full attention, traced" })).getAttribute("aria-pressed")).toBe(
			"true",
		);
	});

	it("keeps another run open when a delete started before it finishes", async () => {
		stubRoutes();
		let finish: () => void = () => undefined;
		const finished = new Promise<void>((resolve) => {
			finish = resolve;
		});
		server.use(
			http.delete(localApiPath(`model-lab/experiments/${traceId}`), async () => {
				await finished;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		const { router } = await renderRoute(`/model-lab?tab=trace&experiment=${traceId}`);
		await screen.findByTestId("model-lab-trace-view");
		// A finished delete refreshes the list: the request after it is the sign it is done.
		let listRequests = 0;
		server.use(
			http.get(localApiPath("model-lab/experiments"), () => {
				listRequests++;
				return HttpResponse.json({
					platformSupported: true,
					unsupportedMessage: null,
					activeExperimentId: null,
					maxRetainedExperiments: 20,
					maxPinnedExperiments: 20,
					experiments: [otherRow],
				});
			}),
		);

		const [deleteFirst] = screen.getAllByRole("button", { name: "Delete" });
		await openConfirm(deleteFirst as HTMLElement, "Delete this experiment?");
		acceptConfirm();
		fireEvent.click(screen.getAllByRole("button", { name: "Qwen/Qwen3.5-0.8B" })[1] as HTMLElement);
		await waitFor(() => expect(searchOf(router)["experiment"]).toBe(otherTraceId));

		await act(async () => finish());

		await waitFor(() => expect(listRequests).toBeGreaterThan(0));
		expect(searchOf(router)).toEqual({ experiment: otherTraceId, tab: "trace" });
		expect(screen.getByTestId("model-lab-trace-view").textContent).toContain(otherPrompt);
	});

	it("leaves the page the operator went to alone when a delete finishes after they left", async () => {
		stubRoutes();
		let finish: () => void = () => undefined;
		const finished = new Promise<void>((resolve) => {
			finish = resolve;
		});
		server.use(
			http.delete(localApiPath(`model-lab/experiments/${traceId}`), async () => {
				await finished;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		const { router, history, queryClient } = await renderRoute(`/model-lab?tab=trace&experiment=${traceId}`);
		await screen.findByTestId("model-lab-trace-view");
		const [deleteFirst] = screen.getAllByRole("button", { name: "Delete" });
		await openConfirm(deleteFirst as HTMLElement, "Delete this experiment?");
		acceptConfirm();
		await waitFor(() => expect(queryClient.isMutating()).toBe(1));

		await act(async () => history.push("/outside"));
		await screen.findByText("Outside");
		const left = history.length;
		await act(async () => finish());

		await waitFor(() => expect(queryClient.isMutating()).toBe(0));
		await act(async () => undefined);
		expect(history.location.pathname).toBe("/outside");
		expect(router.state.location.href).toBe("/outside");
		expect(history.length).toBe(left);
		expect(screen.getByText("Outside")).toBeTruthy();
	});

	it("leaves a navigation away alone when a delete finishes while the other page still loads", async () => {
		stubRoutes();
		let finish: () => void = () => undefined;
		const finished = new Promise<void>((resolve) => {
			finish = resolve;
		});
		server.use(
			http.delete(localApiPath(`model-lab/experiments/${traceId}`), async () => {
				await finished;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		let load: () => void = () => undefined;
		const loaded = new Promise<void>((resolve) => {
			load = resolve;
		});
		const { router, history, queryClient } = await renderRoute(`/model-lab?tab=trace&experiment=${traceId}`, loaded);
		await screen.findByTestId("model-lab-trace-view");
		const [deleteFirst] = screen.getAllByRole("button", { name: "Delete" });
		await openConfirm(deleteFirst as HTMLElement, "Delete this experiment?");
		acceptConfirm();
		await waitFor(() => expect(queryClient.isMutating()).toBe(1));

		act(() => history.push("/outside"));
		await waitFor(() => expect(router.state.status).toBe("pending"));
		const length = history.length;
		await act(async () => finish());
		await waitFor(() => expect(queryClient.isMutating()).toBe(0));
		await act(async () => undefined);
		// The delete finished while Model Lab was still the page shown.
		expect(screen.queryByText("Outside")).toBeNull();
		await act(async () => load());

		expect(await screen.findByText("Outside")).toBeTruthy();
		expect(history.location.pathname).toBe("/outside");
		expect(router.state.location.href).toBe("/outside");
		expect(history.length).toBe(length);
	});

	it("hands a blocked jump over nowhere: focus and scroll stay, and Forward keeps the edited draft", async () => {
		stubRoutes();
		const { history } = await renderRoute(`/model-lab?tab=trace&experiment=${otherTraceId}`);
		await screen.findByTestId("model-lab-trace-view");
		fireEvent.click(await screen.findByTestId("model-lab-trace-try-next-token"));
		await waitFor(() => expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe(otherPrompt));
		fireEvent.change(screen.getByLabelText("System prompt (optional)"), { target: { value: "Be brief." } });
		fireEvent.change(screen.getByLabelText("Prompt"), { target: { value: "My draft." } });
		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));

		// The tab bar has scrolled above the page's scroller, so a shown jump would bring it back to the top.
		const scroller = screen.getByTestId("page-scroller");
		scroller.scrollTop = 600;
		const rects = vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
			return { top: this === scroller ? 0 : -100 } as DOMRect;
		});
		const tryAgain = await screen.findByTestId("model-lab-trace-try-next-token");
		tryAgain.focus();
		const unblock = history.block({ blockerFn: () => true });
		try {
			fireEvent.click(tryAgain);
			await act(async () => undefined);

			expect(selectedTab()).toBe("Trace and steer");
			expect(document.activeElement).toBe(tryAgain);
			expect(scroller.scrollTop).toBe(600);
		} finally {
			unblock();
			rects.mockRestore();
		}

		await act(async () => history.forward());
		await waitFor(() => expect(selectedTab()).toBe("Next token"));
		expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe("My draft.");
		expect((screen.getByLabelText("System prompt (optional)") as HTMLTextAreaElement).value).toBe("Be brief.");
	});

	it("writes a chip as a plain range token that a reload reads back, with the chip open", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}`);
		fireEvent.click(await screen.findByRole("button", { name: "0–2 · Full attention ×3" }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: "0-2", model: gguf }));
		const href = router.state.location.href;
		expect(href).toBe(`/model-lab?model=${encodeURIComponent(gguf)}&block=0-2`);

		// A reload: the page starts again from that URL alone.
		cleanup();
		const reloaded = await renderRoute(href);

		expect((await screen.findByRole("button", { name: "0–2 · Full attention ×3" })).getAttribute("aria-pressed")).toBe("true");
		expect(searchOf(reloaded.router)).toEqual({ block: "0-2", model: gguf });
		expect(screen.getByRole("heading", { name: "Blocks 0–2" })).toBeTruthy();
	});

	it("moves focus to a block's heading opened from a chip's detail once the router shows it", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&block=0-2`);
		fireEvent.click(await screen.findByRole("button", { name: "Open block 2" }));

		await waitFor(() => expect(searchOf(router)).toEqual({ block: 2, model: gguf }));
		await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Block 2: Full attention" })));
	});

	it("moves focus to a chip's heading picked in the graph once the router shows it", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}`);
		fireEvent.click(await screen.findByTestId("model-lab-architecture-graph-switch"));

		fireEvent.click(await screen.findByTestId("react-flow-node-0-2"));

		await waitFor(() => expect(searchOf(router)).toEqual({ block: "0-2", model: gguf }));
		await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("heading", { name: "Blocks 0–2" })));
	});

	it("leaves no focus behind from a blocked detail pick: a later arrival at that block keeps focus where it is", async () => {
		stubRoutes();
		const { router, history } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&block=0-2`);
		const open = await screen.findByRole("button", { name: "Open block 2" });
		open.focus();
		const unblock = history.block({ blockerFn: () => true });
		try {
			fireEvent.click(open);
			await act(async () => undefined);
			expect(searchOf(router)).toEqual({ block: "0-2", model: gguf });
		} finally {
			unblock();
		}

		// An arrival the operator did not ask for from a detail, as Back or Forward brings one.
		await act(async () => history.push(`/model-lab?model=${encodeURIComponent(gguf)}&block=2`));

		const heading = await screen.findByRole("heading", { name: "Block 2: Full attention" });
		expect(searchOf(router)).toEqual({ block: 2, model: gguf });
		expect(document.activeElement).not.toBe(heading);
	});

	it("goes Back from a block jump to the tab and chip the operator left", async () => {
		stubRoutes();
		const { router, history } = await renderRoute(
			`/model-lab?tab=trace&model=${encodeURIComponent(gguf)}&block=0-2&experiment=${otherTraceId}`,
		);
		await screen.findByTestId("model-lab-trace-table");

		fireEvent.click(screen.getByRole("button", { name: "Show block 0 in Architecture" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(searchOf(router)).toEqual({ block: 0, experiment: otherTraceId, model: otherModel, trace: otherTraceId });

		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(searchOf(router)).toEqual({ block: "0-2", experiment: otherTraceId, model: gguf, tab: "trace" });
		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		expect((await screen.findByRole("button", { name: "0–2 · Full attention ×3" })).getAttribute("aria-pressed")).toBe("true");
	});

	it("keeps the page's scroller where it is across a block and a level change", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&block=0-2`);
		const block = await screen.findByRole("button", { name: "Block 1: Full attention" });
		const scroller = screen.getByTestId("page-scroller");
		scroller.scrollTop = 600;
		// The router restores only what it saw scroll.
		fireEvent.scroll(scroller);

		fireEvent.click(block);
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, model: gguf }));
		expect(scroller.scrollTop).toBe(600);

		fireEvent.click(within(await screen.findByTestId("model-lab-architecture-detail")).getByRole("radio", { name: "Detail" }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, level: "detail", model: gguf }));
		expect(scroller.scrollTop).toBe(600);
	});
});

describe("Model Lab block detail in the URL", () => {
	it("writes each pick inside a block as one change that replaces the entry: a tensor, a solid node, a dashed node, the list and a group", async () => {
		stubRoutes();
		stubDetailed();
		const { router, history } = await renderRoute(blockUrl());
		const entered = history.length;

		fireEvent.click(await screen.findByRole("button", { name: "attn_k.weight" }));
		await waitFor(() =>
			expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, tensor: "blk.1.attn_k.weight" }),
		);
		expect(screen.getByTestId("model-lab-architecture-tensor").textContent).toContain("blk.1.attn_k.weight");

		// A solid node opens its operation and selects its group in one change, and the tensor goes.
		fireEvent.click(screen.getByTestId("react-flow-node-attn"));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, op: "attn" }));
		expect(screen.queryByTestId("model-lab-architecture-tensor")).toBeNull();
		expect(groupSelected("blk.1.attention")).toBe("true");
		expect(operation("Self-attention").getAttribute("aria-expanded")).toBe("true");

		// A dashed node and the list change only the operation.
		fireEvent.click(screen.getByTestId("react-flow-node-input"));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, op: "input" }));
		fireEvent.click(operation("Feed-forward"));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, op: "ffn" }));

		fireEvent.click(screen.getByRole("button", { name: /blk\.1\.ffn/ }));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.ffn", model: gguf, op: "ffn" }));
		expect(groupSelected("blk.1.attention")).toBe("false");
		expect(history.length).toBe(entered);
	});

	it("opens a link's operation, group and tensor, and scrolls to the operation once", async () => {
		stubRoutes();
		stubDetailed();
		const { router } = await renderRoute(blockUrl("&op=ffn&group=blk.1.ffn&tensor=blk.1.ffn_down.weight"));

		expect((await screen.findByTestId("model-lab-architecture-tensor")).textContent).toContain("blk.1.ffn_down.weight");
		expect(screen.getByRole("button", { name: "ffn_down.weight" }).getAttribute("aria-pressed")).toBe("true");
		expect(groupSelected("blk.1.ffn")).toBe("true");
		expect(groupSelected("blk.1.attention")).toBe("false");
		expect(screen.getByTestId("react-flow-node-ffn")).toBeTruthy();
		expect(operation("Feed-forward").getAttribute("aria-expanded")).toBe("true");
		expect(scroll().mock.contexts).toEqual([operation("Feed-forward").closest("[data-item-id]")]);
		expect(scroll()).toHaveBeenCalledExactlyOnceWith({ block: "center", behavior: "smooth" });
		expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.ffn", model: gguf, op: "ffn", tensor: "blk.1.ffn_down.weight" });
	});

	it("lets a link's tensor decide its group, and ignores what names nothing in the block without writing back", async () => {
		stubRoutes();
		stubDetailed();
		const mismatched = await renderRoute(blockUrl("&group=blk.1.ffn&tensor=blk.1.attn_k.weight"));

		expect((await screen.findByTestId("model-lab-architecture-tensor")).textContent).toContain("blk.1.attn_k.weight");
		expect(groupSelected("blk.1.attention")).toBe("true");
		expect(groupSelected("blk.1.ffn")).toBe("false");
		expect(searchOf(mismatched.router)).toEqual({ block: 1, group: "blk.1.ffn", model: gguf, tensor: "blk.1.attn_k.weight" });

		cleanup();
		// Another block's group and tensor, and an operation this template does not have.
		const unknown = await renderRoute(blockUrl("&op=router&group=blk.0.attention&tensor=blk.0.attn_q.weight"));

		await screen.findByRole("heading", { name: "Block 1: Full attention" });
		expect(screen.queryByTestId("model-lab-architecture-tensor")).toBeNull();
		expect(groupSelected("blk.1.attention")).toBe("false");
		expect(groupSelected("blk.1.ffn")).toBe("false");
		for (const name of ["Residual in", "Self-attention", "Feed-forward"]) {
			expect(operation(name).getAttribute("aria-expanded")).toBe("false");
		}
		expect(scroll()).not.toHaveBeenCalled();
		expect(searchOf(unknown.router)).toEqual({
			block: 1,
			group: "blk.0.attention",
			model: gguf,
			op: "router",
			tensor: "blk.0.attn_q.weight",
		});
	});

	it("restores the operation, group and tensor on Back after a jump, scrolling once to the operation it arrives at", async () => {
		stubRoutes();
		stubDetailed();
		const inside = { group: "blk.1.attention", op: "attn", tensor: "blk.1.attn_q.weight" };
		const { router, history } = await renderRoute(
			blockUrl(`&trace=${traceId}&op=attn&group=blk.1.attention&tensor=blk.1.attn_q.weight`),
		);
		await screen.findByTestId("model-lab-architecture-tensor");

		fireEvent.click(await screen.findByRole("button", { name: "Open trace" }));
		await screen.findByTestId("model-lab-trace-table");
		fireEvent.click(screen.getByRole("button", { name: "Show block 0 in Architecture" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		// Another block: the last one's selection goes.
		expect(searchOf(router)).toEqual({ block: 0, experiment: traceId, model: gguf, trace: traceId });
		await screen.findByRole("heading", { name: "Block 0: Full attention" });
		expect(screen.queryByTestId("model-lab-architecture-tensor")).toBeNull();
		scroll().mockClear();

		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));

		expect(searchOf(router)).toEqual({ block: 1, model: gguf, trace: traceId, ...inside });
		await screen.findByRole("heading", { name: "Block 1: Full attention" });
		expect(screen.getByTestId("model-lab-architecture-tensor").textContent).toContain("blk.1.attn_q.weight");
		expect(groupSelected("blk.1.attention")).toBe("true");
		expect(operation("Self-attention").getAttribute("aria-expanded")).toBe("true");
		expect(scroll().mock.contexts).toEqual([operation("Self-attention").closest("[data-item-id]")]);
	});

	it("scrolls the open operation back into view once each time the tab shows the same kept detail again", async () => {
		stubRoutes();
		stubDetailed();
		const { router, history } = await renderRoute(blockUrl(`&trace=${traceId}&op=attn`));
		const heading = await screen.findByRole("heading", { name: "Block 1: Full attention" });
		const item = operation("Self-attention").closest("[data-item-id]");
		expect(scroll().mock.contexts).toEqual([item]);

		fireEvent.click(await screen.findByRole("button", { name: "Open trace" }));
		await screen.findByTestId("model-lab-trace-table");
		// Subscribed after the router's scroll restoration, which sets the scroller on Back once the page has rendered and
		// so cancels a smooth scroll begun before it. jsdom has no layout: only this order is provable here.
		const rendered = vi.fn();
		router.subscribe("onRendered", rendered);
		scroll().mockClear();
		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));

		expect(searchOf(router)).toEqual({ block: 1, model: gguf, op: "attn", trace: traceId });
		// The same detail, never remounted: only the tab showing it again scrolls, after the router's restoration.
		expect(screen.getByRole("heading", { name: "Block 1: Full attention" })).toBe(heading);
		expect(operation("Self-attention").getAttribute("aria-expanded")).toBe("true");
		expect(scroll().mock.contexts).toEqual([item]);
		expect(scroll()).toHaveBeenCalledExactlyOnceWith({ block: "center", behavior: "smooth" });
		expect(rendered).toHaveBeenCalledOnce();
		expect(scroll()).toHaveBeenCalledAfter(rendered);

		// A tab click away and back scrolls once more; a list close and reopen after it does not.
		fireEvent.click(screen.getByRole("tab", { name: "Inspect" }));
		await waitFor(() => expect(selectedTab()).toBe("Inspect"));
		fireEvent.click(screen.getByRole("tab", { name: "Architecture" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(scroll().mock.contexts).toEqual([item, item]);
		fireEvent.click(operation("Self-attention"));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, model: gguf, trace: traceId }));
		fireEvent.click(operation("Self-attention"));
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, model: gguf, op: "attn", trace: traceId }));
		expect(scroll()).toHaveBeenCalledTimes(2);
	});

	it("follows Back and Forward within one block: the operation, group, tensor and code change in the same detail", async () => {
		stubRoutes();
		stubDetailed(true);
		const { router, history } = await renderRoute(blockUrl("&group=blk.1.attention"));
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 210–280`));
		const heading = screen.getByRole("heading", { name: "Block 1: Full attention" });

		await act(async () => history.push(blockUrl("&op=ffn&group=blk.1.ffn&tensor=blk.1.ffn_down.weight")));
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 290–310`));
		expect(groupSelected("blk.1.ffn")).toBe("true");
		expect(screen.getByTestId("model-lab-architecture-tensor").textContent).toContain("blk.1.ffn_down.weight");
		expect(operation("Feed-forward").getAttribute("aria-expanded")).toBe("true");

		await act(async () => history.back());
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 210–280`));
		expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf });
		expect(groupSelected("blk.1.attention")).toBe("true");
		expect(screen.queryByTestId("model-lab-architecture-tensor")).toBeNull();
		expect(operation("Feed-forward").getAttribute("aria-expanded")).toBe("false");
		// Within one block the detail stays mounted; only what it shows follows the URL.
		expect(screen.getByRole("heading", { name: "Block 1: Full attention" })).toBe(heading);

		await act(async () => history.forward());
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 290–310`));
		expect(groupSelected("blk.1.ffn")).toBe("true");
		expect(operation("Feed-forward").getAttribute("aria-expanded")).toBe("true");
	});

	it("returns the code to a group clicked again after a module pick, and keeps the module when that click is blocked", async () => {
		stubRoutes();
		stubDetailed(true);
		const { router, history } = await renderRoute(blockUrl());
		const attention = await screen.findByRole("button", { name: /blk\.1\.attention/ });
		const pickBlockModule = async () =>
			fireEvent.click(within(await screen.findByTestId("model-lab-pytorch-modules")).getByRole("button", { name: "(block)" }));

		fireEvent.click(attention);
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 210–280`));
		await pickBlockModule();
		expect(codeLines()).toBe(`${modeling}, lines 400–450`);

		// The URL stays the same, yet the click asks for the group's code again.
		fireEvent.click(attention);
		await waitFor(() => expect(codeLines()).toBe(`${modeling}, lines 210–280`));
		expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf });

		await pickBlockModule();
		const unblock = history.block({ blockerFn: () => true });
		try {
			fireEvent.click(attention);
			await act(async () => undefined);
			expect(codeLines()).toBe(`${modeling}, lines 400–450`);
		} finally {
			unblock();
		}
	});

	it("scrolls to a graph pick each time the router shows it, never for a blocked pick or a list close and reopen", async () => {
		stubRoutes();
		stubDetailed();
		const { router, history } = await renderRoute(blockUrl());
		const attn = await screen.findByTestId("react-flow-node-attn");
		expect(scroll()).not.toHaveBeenCalled();

		fireEvent.click(attn);
		await waitFor(() => expect(scroll()).toHaveBeenCalledOnce());
		const item = operation("Self-attention").closest("[data-item-id]");
		expect(scroll().mock.contexts).toEqual([item]);
		expect(scroll()).toHaveBeenCalledWith({ block: "center", behavior: "smooth" });
		// The same pick again changes no URL, yet the operator asked to see it again.
		fireEvent.click(attn);
		await waitFor(() => expect(scroll()).toHaveBeenCalledTimes(2));
		expect(scroll().mock.contexts).toEqual([item, item]);

		// Closed and opened again from the list, as Enter on the focused control does: it is already where the operator is.
		const control = operation("Self-attention");
		control.focus();
		fireEvent.click(control);
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf }));
		fireEvent.click(control);
		await waitFor(() => expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, op: "attn" }));
		expect(scroll()).toHaveBeenCalledTimes(2);

		const scroller = screen.getByTestId("page-scroller");
		scroller.scrollTop = 600;
		const ffn = screen.getByTestId("react-flow-node-ffn");
		ffn.focus();
		const unblock = history.block({ blockerFn: () => true });
		try {
			fireEvent.click(ffn);
			await act(async () => undefined);

			expect(searchOf(router)).toEqual({ block: 1, group: "blk.1.attention", model: gguf, op: "attn" });
			expect(document.activeElement).toBe(ffn);
			expect(scroller.scrollTop).toBe(600);
			expect(scroll()).toHaveBeenCalledTimes(2);
		} finally {
			unblock();
		}
	});

	it("scrolls without motion to an arrival's operation when the operator asks for reduced motion", async () => {
		stubRoutes();
		stubDetailed();
		const { history } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}`);
		await screen.findByTestId("model-lab-architecture-overview");
		const matchMedia = window.matchMedia;
		window.matchMedia = vi.fn((query: string) => ({
			...matchMedia(query),
			matches: query === "(prefers-reduced-motion: reduce)",
		}));
		try {
			await act(async () => history.push(blockUrl("&op=attn")));
			await screen.findByRole("heading", { name: "Block 1: Full attention" });

			expect(scroll()).toHaveBeenCalledExactlyOnceWith({ block: "center", behavior: "auto" });
			expect(scroll().mock.contexts).toEqual([operation("Self-attention").closest("[data-item-id]")]);
		} finally {
			window.matchMedia = matchMedia;
		}
	});

	it("keeps an outside detail's group and tensor in the URL: a reload restores them, the whole model drops them", async () => {
		stubRoutes();
		const { router } = await renderRoute(`/model-lab?model=${encodeURIComponent(gguf)}&block=embedding`);

		fireEvent.click(await screen.findByRole("button", { name: "token_embd.weight" }));
		await waitFor(() =>
			expect(searchOf(router)).toEqual({ block: "embedding", group: "embedding", model: gguf, tensor: "token_embd.weight" }),
		);
		const href = router.state.location.href;

		cleanup();
		const reloaded = await renderRoute(href);

		expect((await screen.findByTestId("model-lab-architecture-tensor")).textContent).toContain("token_embd.weight");
		expect(groupSelected("embedding")).toBe("true");
		fireEvent.click(screen.getByRole("button", { name: "Model" }));
		await waitFor(() => expect(searchOf(reloaded.router)).toEqual({ model: gguf }));
		expect(screen.queryByTestId("model-lab-architecture-tensor")).toBeNull();
	});
});

describe("the training stepper on its route", () => {
	const runA = "77777777-7777-4777-8777-777777777777";
	const runB = "88888888-8888-4888-8888-888888888888";
	const savedRun = (id: string, steps: number) => ({
		id,
		template: playgroundFixture.summary.template,
		config: { ...playgroundFixture.summary.config, steps },
		outcome: playgroundFixture.summary.outcome,
		finalLoss: playgroundFixture.summary.finalLoss,
		finalAccuracy: playgroundFixture.summary.finalAccuracy,
		traceByteCount: 10,
		createdAtUtc: "2026-11-25T00:00:00Z",
	});
	// Run A and run B differ in their step count, so each row and each open run can be told apart.
	const runs = [savedRun(runA, 8), savedRun(runB, 9)];
	const rowName = (steps: number) => `XOR (4 points), depth 2, 4 hidden units, ${steps} steps, learning rate 0.5, seed 0`;

	function stubPlayground(): void {
		stubRoutes();
		server.use(
			http.get(localApiPath("model-lab/playground/runs"), () =>
				HttpResponse.json({ platformSupported: true, unsupportedMessage: null, maxRetainedRuns: 20, runs }),
			),
			http.get(localApiPath("model-lab/playground/runs/:runId"), ({ params }) => {
				const run = runs.find((item) => item.id === params["runId"]);
				return HttpResponse.json({ run, trace: { ...playgroundFixture.trace, config: run?.config } });
			}),
			// The page's model picker finds no GGUF at all.
			http.get(localApiPath("models"), () => HttpResponse.json({ isAvailable: true, items: [] })),
		);
	}

	const openSteps = () => within(screen.getByTestId("model-lab-playground-stepper")).getByTestId("model-lab-playground-position");

	it("reopens the URL's run on a reload with no GGUF installed, and a run picked from the list replaces its entry", async () => {
		stubPlayground();
		const { router, history } = await renderRoute(`/model-lab?tab=playground&run=${runA}`);

		await waitFor(() => expect(selectedTab()).toBe("Training stepper"));
		await waitFor(() => expect(openSteps().textContent).toContain("training step 1 of 8"));
		expect(screen.queryByTestId("model-lab-no-models")).toBeNull();
		const entries = history.length;

		fireEvent.click(await screen.findByRole("button", { name: rowName(9) }));

		await waitFor(() => expect(searchOf(router)).toEqual({ run: runB, tab: "playground" }));
		await waitFor(() => expect(openSteps().textContent).toContain("training step 1 of 9"));
		expect(history.length).toBe(entries);

		const href = router.state.location.href;
		cleanup();
		await renderRoute(href);
		await waitFor(() => expect(openSteps().textContent).toContain("training step 1 of 9"));
	});

	it("keeps the open run through a jump on another tab and Back", async () => {
		stubPlayground();
		const { router, history } = await renderRoute(`/model-lab?tab=playground&run=${runA}`);
		await waitFor(() => expect(openSteps().textContent).toContain("training step 1 of 8"));

		fireEvent.click(screen.getByRole("tab", { name: "Trace and steer" }));
		fireEvent.click((await screen.findAllByRole("button", { name: "Qwen/Qwen3.5-0.8B" }))[1] as HTMLElement);
		await screen.findByTestId("model-lab-trace-table");
		const before = history.length;
		fireEvent.click(screen.getByRole("button", { name: "Show block 0 in Architecture" }));
		await waitFor(() => expect(selectedTab()).toBe("Architecture"));
		expect(history.length).toBe(before + 1);

		await act(async () => history.back());
		await waitFor(() => expect(selectedTab()).toBe("Trace and steer"));
		expect(searchOf(router)).toEqual({ experiment: otherTraceId, run: runA, tab: "trace" });
		fireEvent.click(screen.getByRole("tab", { name: "Training stepper" }));
		await waitFor(() => expect(selectedTab()).toBe("Training stepper"));
		expect(openSteps().textContent).toContain("training step 1 of 8");
	});

	it("keeps another run open when a delete started before it finishes", async () => {
		stubPlayground();
		let finish: () => void = () => undefined;
		const finished = new Promise<void>((resolve) => {
			finish = resolve;
		});
		server.use(
			http.delete(localApiPath(`model-lab/playground/runs/${runA}`), async () => {
				await finished;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		const { router, queryClient } = await renderRoute(`/model-lab?tab=playground&run=${runA}`);
		await waitFor(() => expect(openSteps().textContent).toContain("training step 1 of 8"));

		await openConfirm(screen.getByRole("button", { name: /^Delete XOR .* 8 steps/ }), "Delete this run?");
		acceptConfirm();
		await waitFor(() => expect(queryClient.isMutating()).toBe(1));
		fireEvent.click(screen.getByRole("button", { name: rowName(9) }));
		await waitFor(() => expect(searchOf(router)["run"]).toBe(runB));

		await act(async () => finish());

		await waitFor(() => expect(queryClient.isMutating()).toBe(0));
		await act(async () => undefined);
		expect(searchOf(router)).toEqual({ run: runB, tab: "playground" });
		expect(openSteps().textContent).toContain("training step 1 of 9");
	});
});
