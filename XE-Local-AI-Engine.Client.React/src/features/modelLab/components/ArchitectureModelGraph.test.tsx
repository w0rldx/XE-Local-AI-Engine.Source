// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { fireEvent, screen, within } from "@testing-library/react";
import type { Edge, Node } from "@xyflow/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { ArchitectureModelGraph } from "@/features/modelLab/components/ArchitectureModelGraph";
import { ArchitectureOverview } from "@/features/modelLab/components/ArchitectureOverview";
import { ArchitecturePyTorchGraph } from "@/features/modelLab/components/ArchitecturePyTorchGraph";
import { ArchitectureTemplateGraph } from "@/features/modelLab/components/ArchitectureTemplateGraph";
import type {
	ArchitectureBlock,
	ArchitectureGroup,
	ArchitectureSelection,
	ArchitectureTemplate,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { buildArchitectureHierarchy } from "@/features/modelLab/models/ModelLabHierarchy";
import { testMantineTheme } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

// jsdom gives a canvas no size, so the mock lays the props the graph hands React Flow out as plain elements.
vi.mock("@xyflow/react", () => ({
	Background: () => null,
	Controls: () => null,
	ReactFlow: ({
		id,
		colorMode,
		nodes,
		edges,
		onNodeClick,
		fitViewOptions,
		nodesFocusable,
		panOnDrag,
		zoomOnScroll,
		preventScrolling,
		ariaLabelConfig,
	}: {
		id?: string;
		colorMode?: string;
		nodes: Node<{ label: ReactNode }>[];
		edges: Edge[];
		onNodeClick?: (event: unknown, node: Node) => void;
		fitViewOptions?: { minZoom?: number; maxZoom?: number; padding?: string };
		nodesFocusable?: boolean;
		panOnDrag?: boolean;
		zoomOnScroll?: boolean;
		preventScrolling?: boolean;
		ariaLabelConfig?: Record<string, unknown>;
		children?: ReactNode;
	}) => (
		<div
			data-testid="react-flow"
			data-flow-id={id}
			data-color-mode={colorMode}
			data-fit-min-zoom={fitViewOptions?.minZoom}
			data-fit-max-zoom={fitViewOptions?.maxZoom}
			data-fit-padding={fitViewOptions?.padding}
			data-nodes-focusable={String(nodesFocusable)}
			data-gestures={JSON.stringify({ panOnDrag, zoomOnScroll, preventScrolling })}
			data-controls-label={String(ariaLabelConfig?.["controls.ariaLabel"])}
		>
			{nodes.map((node) => (
				<button
					key={node.id}
					type="button"
					data-testid={`react-flow-node-${node.id}`}
					data-selected={String(node.selected)}
					data-x={node.position.x}
					data-y={node.position.y}
					data-width={node.style?.width}
					data-source-position={node.sourcePosition}
					data-target-position={node.targetPosition}
					aria-hidden={node.domAttributes?.["aria-hidden"]}
					onClick={(event) => onNodeClick?.(event, node)}
				>
					{node.data.label}
				</button>
			))}
			{edges.map((edge) => (
				<span
					key={edge.id}
					data-testid="react-flow-edge"
					data-source={edge.source}
					data-target={edge.target}
					data-marker-end={typeof edge.markerEnd === "object" ? edge.markerEnd.type : edge.markerEnd}
				>
					{edge.id}
				</span>
			))}
		</div>
	),
}));

type Values = NonNullable<Parameters<typeof buildArchitectureHierarchy>[0]["hyperparameters"]>;

const header = (blockCount: number, nextnPredictLayers: number | null = null): Values => ({
	fileType: "Q4_K_M",
	declaredParamCount: null,
	blockCount,
	nextnPredictLayers,
	splitCount: null,
	attentionHeadCount: null,
	attentionHeadCountKv: null,
	attentionKeyLength: null,
	attentionValueLength: null,
	embeddingLength: null,
	contextLength: null,
	expertCount: null,
	expertUsedCount: null,
	fullAttentionInterval: null,
});

const kinds: Record<string, string> = { L: "linearAttention", F: "fullAttention", M: "mtp" };

const block = (index: number, kind: string): ArchitectureBlock => ({
	index,
	kind,
	byteSize: null,
	groups: [
		{
			componentId: `blk.${index}.attention`,
			component: "attention",
			provenance: "read",
			tensors: [
				{
					ggufName: `blk.${index}.attn_q.weight`,
					hfName: null,
					shape: [64, 64],
					ggmlType: "Q4_K",
					byteSize: null,
					valueRelation: null,
				},
			],
		},
	],
});

const blocks = (pattern: string, indices?: number[]) =>
	[...pattern].map((letter, at) => block(indices?.[at] ?? at, kinds[letter] ?? "other"));

const group = (componentId: string, component = componentId): ArchitectureGroup => ({
	componentId,
	component,
	provenance: "read",
	tensors: [
		{ ggufName: `${componentId}.weight`, hfName: null, shape: [64], ggmlType: "F32", byteSize: null, valueRelation: null },
	],
});
const ends = [group("embedding"), group("output_norm", "norm"), group("output")];

const source = "llama.cpp src/models/qwen35.cpp @ b12014";
const template = (kind: string): ArchitectureTemplate => ({
	kind,
	provenance: "inferred",
	source,
	nodes: [{ id: "input", label: "Residual in", component: null }],
	edges: [],
});

interface Case {
	blocks: ArchitectureBlock[];
	values: Values;
	nonBlock?: ArchitectureGroup[];
	support?: string;
	templates?: ArchitectureTemplate[];
}

const qwen: Case = {
	blocks: [...blocks("LLLFLLLF"), block(8, "mtp")],
	values: header(9, 1),
	templates: [template("linearAttention"), template("fullAttention")],
};

function hierarchyOf(item: Case) {
	const built = buildArchitectureHierarchy({
		support: item.support ?? "detailed",
		blocks: item.blocks,
		nonBlock: item.nonBlock ?? ends,
		hyperparameters: item.values,
	});
	if (built === null) {
		throw new Error("a readable file builds a hierarchy");
	}
	return built;
}

// The row and the graph from one hierarchy, as the explorer shows them.
function renderBoth(
	item: Case,
	{
		selection = null,
		tracedBlocks = null,
		routedBlocks = null,
		onSelect = vi.fn(),
	}: {
		selection?: ArchitectureSelection | null;
		tracedBlocks?: ReadonlySet<number> | null;
		routedBlocks?: ReadonlySet<number> | null;
		onSelect?: (selection: ArchitectureSelection) => void;
	} = {},
) {
	const hierarchy = hierarchyOf(item);
	renderWithProviders(
		<>
			<ArchitectureModelGraph
				hierarchy={hierarchy}
				selection={selection}
				tracedBlocks={tracedBlocks}
				routedBlocks={routedBlocks}
				templates={item.templates ?? []}
				onSelect={onSelect}
			/>
			<ArchitectureOverview
				hierarchy={hierarchy}
				hasOutputTensor={true}
				split={false}
				selection={selection}
				tracedBlocks={tracedBlocks}
				routedBlocks={routedBlocks}
				routingFailure={null}
				onSelect={onSelect}
			/>
		</>,
	);
	return onSelect;
}

const rowEntryNames = new Map([
	["Embedding", "embedding"],
	["Output", "output"],
	["MTP", "mtp"],
	["Other", "other"],
]);

// What the row offers with nothing selected (so no chip is open): its buttons, by the selection each one makes.
function rowSelections(): string[] {
	const row = screen.getByTestId("model-lab-architecture-overview");
	return within(row)
		.getAllByRole("button")
		.map((button) => {
			const testId = button.getAttribute("data-testid") ?? "";
			const chip = /^model-lab-architecture-chip-(.+)$/.exec(testId)?.[1];
			const cell = /^model-lab-architecture-block-(\d+)$/.exec(testId)?.[1];
			const text = button.textContent ?? "";
			return chip ?? cell ?? rowEntryNames.get(text) ?? (text.startsWith("Blocks: ") ? "stack" : `unknown:${text}`);
		})
		.sort();
}

function graphNodes(): { selectable: string[]; gaps: number } {
	const flow = screen.getByTestId("react-flow");
	const nodes = within(flow).getAllByRole("button", { hidden: true });
	return {
		selectable: nodes
			.filter((node) => node.getAttribute("aria-hidden") !== "true")
			.map((node) => (node.getAttribute("data-testid") ?? "").replace("react-flow-node-", ""))
			.sort(),
		gaps: nodes.filter((node) => node.getAttribute("aria-hidden") === "true").length,
	};
}

const edgeIds = () => screen.queryAllByTestId("react-flow-edge").map((edge) => edge.textContent);

describe("ArchitectureModelGraph", () => {
	it.each<[string, Case, string[], string[]]>([
		[
			"a complete Qwen3.5-like file, chip per unit and MTP apart",
			qwen,
			["0-3", "4-7", "embedding", "mtp", "output", "stack"],
			["embedding>0-3", "0-3>4-7", "4-7>output"],
		],
		[
			"a uniform OLMoE-like file",
			{ blocks: blocks("F".repeat(16)), values: header(16), templates: [template("fullAttention")] },
			["0-15", "embedding", "output", "stack"],
			["embedding>0-15", "0-15>output"],
		],
		[
			"a complete file of runs with a one-block chip",
			{ blocks: blocks("FFFL"), values: header(4), templates: [template("fullAttention")] },
			["0-2", "3", "embedding", "output", "stack"],
			["embedding>0-2", "0-2>3", "3>output"],
		],
		[
			"an overview file, even with a template",
			{ ...qwen, support: "overview" },
			["0-3", "4-7", "embedding", "mtp", "output", "stack"],
			[],
		],
		["a detailed file with no template", { ...qwen, templates: [] }, ["0-3", "4-7", "embedding", "mtp", "output", "stack"], []],
		[
			"a file with only an MTP template",
			{ ...qwen, templates: [template("mtp")] },
			["0-3", "4-7", "embedding", "mtp", "output", "stack"],
			[],
		],
		[
			"a prefix shard",
			{ ...qwen, blocks: blocks("LLLFLLLF"), values: header(16) },
			["0-3", "4-7", "embedding", "output", "stack"],
			[],
		],
		[
			"a leading-missing shard",
			{ ...qwen, blocks: blocks("LLLFLLLF", [8, 9, 10, 11, 12, 13, 14, 15]), values: header(16) },
			// Sorted as text, as the comparison with the row is.
			["12-15", "8-11", "embedding", "output", "stack"],
			[],
		],
		[
			"a middle shard",
			{ ...qwen, blocks: blocks("LLLFLLLF", [4, 5, 6, 7, 8, 9, 10, 11]), values: header(16) },
			["4-7", "8-11", "embedding", "output", "stack"],
			[],
		],
		[
			"a file with only other tensors",
			{ blocks: [], values: header(0), nonBlock: [group("rope_freqs", "other")], templates: [template("fullAttention")] },
			["other"],
			[],
		],
	])("draws the row's entries for %s, with edges only where the trunk's order is established", (_case, item, expected, edges) => {
		renderBoth(item);

		const nodes = graphNodes();
		expect(nodes.selectable).toEqual(expected);
		expect(nodes.selectable).toEqual(rowSelections());
		expect(nodes.gaps).toBe(screen.queryAllByTestId("model-lab-architecture-gap").length);
		expect(edgeIds()).toEqual(edges);
		// Every execution edge ends in the arrowhead the caption names.
		expect(screen.queryAllByTestId("react-flow-edge").map((edge) => edge.dataset["markerEnd"])).toEqual(
			edges.map(() => "arrowclosed"),
		);
		if (edges.length > 0) {
			expect(screen.getByTestId("model-lab-architecture-graph-basis").textContent).toBe(`Inferred from llama.cpp (${source})`);
			expect(screen.queryByTestId("model-lab-architecture-graph-index-order")).toBeNull();
		} else {
			expect(screen.getByTestId("model-lab-architecture-graph-index-order").textContent).toBe(
				"Groups in this file, in index order",
			);
			expect(screen.queryByTestId("model-lab-architecture-graph-basis")).toBeNull();
		}
	});

	it("draws a gap marker for each run of missing blocks, inert and hidden from assistive technology", () => {
		const onSelect = renderBoth({ ...qwen, blocks: blocks("LLLFLLLF", [4, 5, 6, 7, 8, 9, 10, 11]), values: header(16) });

		const gaps = within(screen.getByTestId("react-flow"))
			.getAllByRole("button", { hidden: true })
			.filter((node) => node.getAttribute("aria-hidden") === "true");
		expect(gaps.map((node) => node.getAttribute("data-testid"))).toEqual(["react-flow-node-gap-0", "react-flow-node-gap-end"]);
		expect(gaps.map((node) => node.textContent)).toEqual(["… blocks not in this file …", "… blocks not in this file …"]);
		for (const node of gaps) {
			fireEvent.click(node);
		}
		expect(onSelect).not.toHaveBeenCalled();
	});

	it.each<[string, Case, ArchitectureSelection]>([
		["a unit chip", qwen, "0-3"],
		["a one-block chip", { blocks: blocks("FFFL"), values: header(4) }, 3],
		["Blocks", qwen, "stack"],
		["MTP", qwen, "mtp"],
		["Embedding", qwen, "embedding"],
		["Output", qwen, "output"],
		["Other", { blocks: [], values: header(0), nonBlock: [group("rope_freqs", "other")] }, "other"],
	])("selects what %s selects in the row", (_case, item, target) => {
		const onSelect = renderBoth(item);

		fireEvent.click(screen.getByTestId(`react-flow-node-${target}`));

		expect(onSelect).toHaveBeenCalledExactlyOnceWith(target);
	});

	it.each<[string, ArchitectureSelection, string]>([
		["the selected chip", "4-7", "4-7"],
		["the chip holding the selected block", 5, "4-7"],
		["MTP for its selected block", 8, "mtp"],
		["Blocks", "stack", "stack"],
	])("marks %s and nothing else", (_case, selection, marked) => {
		renderBoth(qwen, { selection });

		const selected = within(screen.getByTestId("react-flow"))
			.getAllByRole("button", { hidden: true })
			.filter((node) => node.getAttribute("data-selected") === "true")
			.map((node) => node.getAttribute("data-testid"));
		expect(selected).toEqual([`react-flow-node-${marked}`]);
	});

	it("shows the row's traced and routed markers: the same block names and the same chip counts", () => {
		renderBoth(
			{ blocks: blocks("FFFL"), values: header(4) },
			{ tracedBlocks: new Set([0, 1, 3]), routedBlocks: new Set([1, 3]) },
		);

		const cell = screen.getByTestId("model-lab-architecture-block-3");
		expect(screen.getByTestId("react-flow-node-3").textContent).toBe(cell.getAttribute("aria-label"));
		expect(cell.getAttribute("aria-label")).toBe("Block 3: Linear attention, traced, routed");
		const chip = screen.getByTestId("model-lab-architecture-chip-0-2").textContent ?? "";
		const node = screen.getByTestId("react-flow-node-0-2").textContent ?? "";
		for (const count of ["2 traced", "1 routed"]) {
			expect(chip).toContain(count);
			expect(node).toContain(count);
		}
	});

	it("opens as a read-only canvas of its own that fits at a readable zoom, with labelled controls", () => {
		renderBoth(qwen);

		const flow = screen.getByTestId("react-flow");
		expect(flow.getAttribute("data-flow-id")).toBe("model-lab-model-graph");
		expect(flow.getAttribute("data-fit-min-zoom")).toBe("0.75");
		expect(flow.getAttribute("data-nodes-focusable")).toBe("false");
		expect(flow.getAttribute("data-controls-label")).toBe("Graph zoom controls");
		// A mouse drag pans; the canvas can be taller than the screen, so the wheel is left to the page.
		expect(JSON.parse(flow.getAttribute("data-gestures") ?? "")).toEqual({
			panOnDrag: true,
			zoomOnScroll: false,
			preventScrolling: false,
		});
	});

	it.each(["light", "dark"] as const)("draws its canvas in the app's %s scheme", (scheme) => {
		renderWithProviders(
			<MantineProvider env="test" theme={testMantineTheme} forceColorScheme={scheme}>
				<ArchitectureModelGraph
					hierarchy={hierarchyOf(qwen)}
					selection={null}
					tracedBlocks={null}
					routedBlocks={null}
					templates={[]}
					onSelect={vi.fn()}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("react-flow").getAttribute("data-color-mode")).toBe(scheme);
	});

	// 11 nodes (Qwen3.5-9B-like: 8 unit chips) and 20 (Qwen3.8-27B-like: 16 unit chips and MTP).
	it.each<[string, Case, number, string[]]>([
		["an 11-node graph", { ...qwen, blocks: blocks("LLLF".repeat(8)), values: header(32) }, 11, ["stack"]],
		[
			"a 20-node graph",
			{ ...qwen, blocks: [...blocks("LLLF".repeat(16)), block(64, "mtp")], values: header(65, 1) },
			20,
			["stack", "mtp"],
		],
	])("lays %s out two per row, inside the canvas at zoom 0.75 or more", (_case, item, count, sideIds) => {
		renderBoth(item);

		const flow = screen.getByTestId("react-flow");
		const nodes = within(flow)
			.getAllByRole("button", { hidden: true })
			.map((node) => ({
				id: (node.getAttribute("data-testid") ?? "").replace("react-flow-node-", ""),
				x: Number(node.dataset["x"]),
				y: Number(node.dataset["y"]),
				width: Number(node.dataset["width"]),
				source: node.dataset["sourcePosition"],
				target: node.dataset["targetPosition"],
			}));
		expect(nodes).toHaveLength(count);
		const minZoom = Number(flow.getAttribute("data-fit-min-zoom"));
		const maxZoom = Number(flow.getAttribute("data-fit-max-zoom"));
		const padding = Number.parseFloat(flow.getAttribute("data-fit-padding") ?? "");
		// Node text wrapped to three lines stays under this.
		const tallestNode = 60;
		const right = Math.max(...nodes.map((node) => node.x + node.width));
		const bottom = Math.max(...nodes.map((node) => node.y)) + tallestNode;
		// A 360 px screen leaves the canvas about 310 px; the fit reaches the floor zoom with every node in view.
		expect(right * minZoom + 2 * padding).toBeLessThanOrEqual(310);
		const canvasHeight = Number.parseFloat(screen.getByTestId("model-lab-architecture-graph-canvas").style.height);
		expect(bottom * maxZoom + 2 * padding).toBeLessThanOrEqual(canvasHeight);
		expect(new Set(nodes.map((node) => node.x)).size).toBe(2);

		// Every edge joins consecutive trunk nodes: along a row left to right, or down from a row's end to the next row's start.
		const at = new Map(nodes.map((node) => [node.id, node]));
		const edges = screen.getAllByTestId("react-flow-edge");
		// The trunk's nodes in a chain: one edge fewer than the trunk has nodes.
		expect(edges).toHaveLength(count - sideIds.length - 1);
		for (const edge of edges) {
			const from = at.get(edge.dataset["source"] ?? "");
			const to = at.get(edge.dataset["target"] ?? "");
			expect(edge.dataset["markerEnd"]).toBe("arrowclosed");
			if (from?.y === to?.y) {
				expect([from?.x, from?.source, to?.x, to?.target]).toEqual([0, "right", 210, "left"]);
			} else {
				expect([from?.x, from?.source, to?.x, to?.target]).toEqual([210, "bottom", 0, "top"]);
				expect((to?.y ?? 0) - (from?.y ?? 0)).toBe(90);
			}
		}
		// Blocks (and MTP) sit below the trunk, unconnected.
		const sides = nodes.filter((node) => sideIds.includes(node.id));
		expect(sides.map((node) => node.id)).toEqual(sideIds);
		const lowestTrunk = Math.max(...nodes.filter((node) => !sides.includes(node)).map((node) => node.y));
		for (const side of sides) {
			expect(side.y).toBeGreaterThan(lowestTrunk);
			expect(edges.some((edge) => edge.dataset["source"] === side.id || edge.dataset["target"] === side.id)).toBe(false);
		}
	});

	it("gives each of Model Lab's canvases its own id, since they can be on screen together", () => {
		const hierarchy = hierarchyOf(qwen);
		renderWithProviders(
			<>
				<ArchitectureModelGraph
					hierarchy={hierarchy}
					selection={null}
					tracedBlocks={null}
					routedBlocks={null}
					templates={[]}
					onSelect={vi.fn()}
				/>
				<ArchitectureTemplateGraph
					template={template("fullAttention")}
					architecture={null}
					groupComponents={new Set()}
					selectedComponent={null}
					onSelectNode={vi.fn()}
				/>
				<ArchitecturePyTorchGraph
					graph={{ graphId: "g", nodes: [{ id: "a", label: "linear" }], edges: [] }}
					rootName="Block"
					selectedNodeId={null}
					onSelectNode={vi.fn()}
				/>
			</>,
		);

		expect(screen.getAllByTestId("react-flow").map((flow) => flow.getAttribute("data-flow-id"))).toEqual([
			"model-lab-model-graph",
			"model-lab-template-graph",
			"model-lab-pytorch-graph",
		]);
	});

	it("draws no canvas past 60 groups and says the row shows them all", () => {
		// 31 one-block segments with a gap between each: 31 chips, 30 gaps, Embedding, Output and Blocks.
		const indices = Array.from({ length: 31 }, (_, at) => at * 2);
		renderBoth({ blocks: blocks("F".repeat(31), indices), values: header(61) });

		expect(screen.queryByTestId("react-flow")).toBeNull();
		expect(screen.getByTestId("model-lab-architecture-graph-too-large").textContent).toBe(
			"This file has 64 groups, too many to draw readably. The row below shows them all.",
		);
		expect(screen.getByTestId("model-lab-architecture-overview")).toBeTruthy();
	});
});
