import { Group, Stack, Text, useComputedColorScheme } from "@mantine/core";
import { Background, Controls, type Edge, type Node, type Position, ReactFlow } from "@xyflow/react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { BasisBadge } from "@/features/modelLab/components/ArchitectureExplanation";
import classes from "@/features/modelLab/components/ArchitectureModelGraph.module.css";
import {
	type ArchitectureSelection,
	type ArchitectureTemplate,
	componentLabel,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { type ArchitectureHierarchy, chipFor } from "@/features/modelLab/models/ModelLabHierarchy";
import { blockCellLabel, chipCounts, chipLabel } from "@/features/modelLab/models/ModelLabHierarchyLabels";

// Past this the canvas is unreadable at any zoom; the row below shows the same groups.
const maxDrawnNodes = 60;

// Two columns, read left to right and row by row: two nodes fit side by side at 0.75 on a 360 px screen (canvas
// about 310 px), and the canvas grows with the rows, so the first fit never has to shrink a long stack.
const columns = 2;
const nodeWidth = 160;
const columnSpacing = 210;
const rowSpacing = 90;
// Room for a label wrapped to three lines.
const nodeHeight = 60;
const fitPadding = 16;
// The canvas is sized for zoom 1; a narrow screen fits the width at 0.75 or more, so 12 px text stays 9 px or more.
const fitViewOptions = { minZoom: 0.75, maxZoom: 1, padding: `${fitPadding}px` } as const;
// A row's left node takes the arrow from the row above and passes it right; the right node passes it down a row.
const handles = [
	{ targetPosition: "top" as Position, sourcePosition: "right" as Position },
	{ targetPosition: "left" as Position, sourcePosition: "bottom" as Position },
];

const nodeStyle = {
	background: "var(--mantine-color-body)",
	color: "var(--mantine-color-text)",
	borderColor: "var(--mantine-color-default-border)",
	fontSize: 12,
	width: nodeWidth,
	// A German label wraps inside the fixed width instead of widening the node.
	whiteSpace: "normal",
	overflowWrap: "anywhere",
} as const;

interface ArchitectureModelGraphProps {
	hierarchy: ArchitectureHierarchy;
	selection: ArchitectureSelection | null;
	tracedBlocks: ReadonlySet<number> | null;
	routedBlocks: ReadonlySet<number> | null;
	// The file's templates (none for an `overview` file); the first trunk one is the source the edges cite.
	templates: readonly ArchitectureTemplate[];
	onSelect: (selection: ArchitectureSelection) => void;
}

// A gap marker has no selection: it is drawn, never chosen.
interface GraphEntry {
	id: string;
	label: string;
	selection: ArchitectureSelection | null;
}

// The overview row's entries as a read-only canvas, at chip level. Edges only where the file and its template establish
// the trunk's order; otherwise the groups stand in index order. The row below stays the keyboard path.
export function ArchitectureModelGraph({
	hierarchy,
	selection,
	tracedBlocks,
	routedBlocks,
	templates,
	onSelect,
}: ArchitectureModelGraphProps) {
	const { t } = useTranslation();
	const colorMode = useComputedColorScheme("light");
	// Edges only where the file holds the whole trunk and a template says how llama.cpp runs it.
	const orderSource = hierarchy.trunkComplete ? (templates.find((candidate) => candidate.kind !== "mtp")?.source ?? null) : null;
	const entry = (target: ArchitectureSelection, label: string): GraphEntry => ({ id: String(target), label, selection: target });
	const gap = (id: string): GraphEntry => ({
		id,
		label: t("pages.modelLab.architecture.hierarchy.gap", "… blocks not in this file …"),
		selection: null,
	});

	// Embedding, the chips with their gaps, Output: the trunk column, in index order.
	const trunk: GraphEntry[] = [
		...(hierarchy.outside.includes("embedding") ? [entry("embedding", componentLabel(t, "embedding"))] : []),
		...hierarchy.segments.flatMap((segment, at) => [
			...(segment.gapBefore ? [gap(`gap-${at}`)] : []),
			...segment.chips.map((chip) => {
				const [only] = chip.blocks;
				if (chip.blocks.length === 1 && only) {
					return entry(chip.selection, blockCellLabel(t, only, tracedBlocks, routedBlocks));
				}
				return entry(chip.selection, [chipLabel(t, chip), ...chipCounts(t, chip, tracedBlocks, routedBlocks)].join(" · "));
			}),
		]),
		...(hierarchy.gapAfter ? [gap("gap-end")] : []),
		...(hierarchy.outside.includes("output") ? [entry("output", componentLabel(t, "output"))] : []),
	];
	// Blocks, MTP and Other: beside the trunk, never connected to it.
	const side: GraphEntry[] = [
		...(hierarchy.trunkBlocks.length > 0
			? [
					entry(
						"stack",
						t("pages.modelLab.architecture.hierarchy.blocksEntry", "Blocks: {{count}} in this file", {
							count: hierarchy.trunkBlocks.length,
						}),
					),
				]
			: []),
		...(hierarchy.mtp.length > 0
			? [entry("mtp", t("pages.modelLab.architecture.hierarchy.graph.mtp", "Multi-token prediction (separate path)"))]
			: []),
		...(hierarchy.outside.includes("other") ? [entry("other", componentLabel(t, "other"))] : []),
	];
	const entries = [...trunk, ...side];

	if (entries.length > maxDrawnNodes) {
		return (
			<Text size="sm" c="dimmed" data-testid="model-lab-architecture-graph-too-large">
				{t(
					"pages.modelLab.architecture.hierarchy.graph.tooLarge",
					"This file has {{count}} groups, too many to draw readably. The row below shows them all.",
					{ count: entries.length },
				)}
			</Text>
		);
	}

	// The row's open chip: the selection itself, or the chip or MTP group holding the selected block.
	const marked =
		typeof selection === "number" && hierarchy.mtp.some((block) => block.index === selection)
			? "mtp"
			: typeof selection === "number"
				? chipFor(hierarchy, selection)?.selection
				: selection;
	const node = (item: GraphEntry, at: number, top: number): Node => {
		const selected = item.selection !== null && item.id === String(marked);
		const column = at % columns;
		return {
			id: item.id,
			position: { x: column * columnSpacing, y: top + Math.floor(at / columns) * rowSpacing },
			...handles[column],
			data: { label: item.label },
			selected,
			selectable: false,
			focusable: false,
			// A gap marker is inert and says nothing a screen reader needs: the row below names the gap.
			...(item.selection === null ? { domAttributes: { "aria-hidden": true } } : {}),
			style: {
				...nodeStyle,
				cursor: item.selection === null ? "default" : "pointer",
				pointerEvents: item.selection === null ? "none" : undefined,
				borderStyle: item.selection === null ? "dashed" : "solid",
				color: item.selection === null ? "var(--mantine-color-dimmed)" : nodeStyle.color,
				outline: selected ? "2px solid var(--mantine-primary-color-filled)" : undefined,
			},
		};
	};
	// The side entries follow the trunk after half a row's gap, unconnected.
	const sideTop = (Math.ceil(trunk.length / columns) + 0.5) * rowSpacing;
	const nodes = [...trunk.map((item, at) => node(item, at, 0)), ...side.map((item, at) => node(item, at, sideTop))];
	const canvasHeight = Math.max(0, ...nodes.map((item) => item.position.y)) + nodeHeight + 2 * fitPadding;
	// A complete trunk has no gap and both ends, so its order is exactly embedding → chips → output.
	const edges: Edge[] =
		orderSource === null
			? []
			: trunk.slice(1).flatMap((item, at) => {
					const from = trunk[at];
					// MarkerType.ArrowClosed's string value: the arrowhead the caption promises, without importing the enum.
					return from
						? [{ id: `${from.id}>${item.id}`, source: from.id, target: item.id, markerEnd: { type: "arrowclosed" } }]
						: [];
				});

	const readOnly = t(
		"pages.modelLab.architecture.hierarchy.graph.readOnly",
		"Read-only graph. Select a group with a pointer, or use the row below to select by keyboard.",
	);
	const ariaLabelConfig = {
		"node.a11yDescription.default": readOnly,
		"node.a11yDescription.keyboardDisabled": readOnly,
		"edge.a11yDescription.default": readOnly,
		"controls.ariaLabel": t("pages.modelLab.architecture.hierarchy.graph.controls", "Graph zoom controls"),
		"controls.zoomIn.ariaLabel": t("pages.modelLab.architecture.hierarchy.graph.zoomIn", "Zoom in"),
		"controls.zoomOut.ariaLabel": t("pages.modelLab.architecture.hierarchy.graph.zoomOut", "Zoom out"),
		"controls.fitView.ariaLabel": t("pages.modelLab.architecture.hierarchy.graph.fitView", "Fit the graph into view"),
	};

	return (
		<Stack gap={4} data-testid="model-lab-architecture-model-graph">
			{orderSource === null ? (
				<Text size="xs" c="dimmed" data-testid="model-lab-architecture-graph-index-order">
					{t("pages.modelLab.architecture.hierarchy.graph.indexOrder", "Groups in this file, in index order")}
				</Text>
			) : (
				<Group gap={6} data-testid="model-lab-architecture-graph-order">
					<Text size="xs" c="dimmed">
						{t("pages.modelLab.architecture.hierarchy.graph.order", "Arrows: the order the groups run in")}
					</Text>
					<BasisBadge basis={{ inferredFrom: orderSource }} data-testid="model-lab-architecture-graph-basis" />
				</Group>
			)}
			<div
				style={{ height: canvasHeight }}
				className={classes["canvas"]}
				data-testid="model-lab-architecture-graph-canvas"
				// panOnDrag admits touchstart even as a button list, and a pointer media query misses hybrid screens: stopping
				// touchstart before it reaches React Flow's pane keeps a one-finger swipe scrolling the page while a mouse drag pans.
				onTouchStartCapture={(event) => event.stopPropagation()}
			>
				<ReactFlow
					id="model-lab-model-graph"
					colorMode={colorMode}
					nodes={nodes}
					edges={edges}
					minZoom={0.1}
					// A mouse drag pans a zoomed-in graph; the canvas can be taller than the screen, so the wheel scrolls the page.
					panOnDrag={true}
					zoomOnScroll={false}
					preventScrolling={false}
					nodesDraggable={false}
					nodesConnectable={false}
					nodesFocusable={false}
					edgesFocusable={false}
					elementsSelectable={false}
					fitView={true}
					fitViewOptions={fitViewOptions}
					proOptions={{ hideAttribution: true }}
					ariaLabelConfig={ariaLabelConfig}
					aria-label={t("pages.modelLab.architecture.hierarchy.graph.label", "Graph of the groups in this file")}
					onNodeClick={(_event, clicked) => {
						const target = entries.find((item) => item.id === clicked.id)?.selection;
						if (target !== null && target !== undefined) {
							onSelect(target);
						}
					}}
				>
					<Background />
					<Controls showInteractive={false} />
				</ReactFlow>
			</div>
		</Stack>
	);
}
