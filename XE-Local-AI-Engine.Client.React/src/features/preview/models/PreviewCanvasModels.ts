import type { Edge, Node } from "@xyflow/react";

import type { PreviewNodeKind, PreviewWorkflowGraph, PreviewWorkflowGraphNode } from "@/features/preview/models/PreviewWorkflowModels";

// React Flow node data carried by every canvas node. The kind drives which node component renders and
// which fields are editable; the agent fields are populated only for Agent nodes (mirrors the flat
// backend node record). `label`/`instructions`/`model`/`modelProfile`/`reasoningEffort` map 1:1 onto
// the wire node fields so the graph mappers stay a straight copy.
export interface PreviewCanvasNodeData extends Record<string, unknown> {
	readonly kind: PreviewNodeKind;
	readonly label?: string;
	readonly instructions?: string;
	readonly model?: string;
	readonly modelProfile?: string;
	readonly reasoningEffort?: string;
}

// React Flow node/edge aliases narrowed to our node data so the canvas + node components share one type.
export type PreviewCanvasNode = Node<PreviewCanvasNodeData>;
export type PreviewCanvasEdge = Edge;

// React Flow type keys — must match the keys registered in the canvas `nodeTypes` map and the
// PreviewNodeKind union (one node component per kind).
export const previewNodeTypeByKind: Record<PreviewNodeKind, string> = {
	Start: "start",
	Agent: "agent",
	Debug: "debug",
	Pause: "pause",
	End: "end",
};

// Maps the backend graph (wire shape) into React Flow nodes/edges. Positions are not part of the wire
// contract, so a freshly loaded graph is auto-laid-out as a simple vertical chain in node order; the
// operator can rearrange on the canvas afterward (positions are view-only, never persisted).
const LAYOUT_X = 240;
const LAYOUT_Y_START = 40;
const LAYOUT_Y_STEP = 140;

export function graphToCanvas(graph: PreviewWorkflowGraph): {
	nodes: PreviewCanvasNode[];
	edges: PreviewCanvasEdge[];
} {
	const nodes: PreviewCanvasNode[] = graph.nodes.map((node, index) => ({
		id: node.id,
		type: previewNodeTypeByKind[node.kind],
		position: { x: LAYOUT_X, y: LAYOUT_Y_START + index * LAYOUT_Y_STEP },
		// Start and End are structural anchors (exactly one of each) — block deletion so a stray Delete keypress
		// cannot break the graph; Agent/Debug/Pause stay freely removable.
		deletable: node.kind !== "Start" && node.kind !== "End",
		data: {
			kind: node.kind,
			label: node.label ?? undefined,
			instructions: node.instructions ?? undefined,
			model: node.model ?? undefined,
			modelProfile: node.modelProfile ?? undefined,
			reasoningEffort: node.reasoningEffort ?? undefined,
		},
	}));

	const edges: PreviewCanvasEdge[] = graph.edges.map((edge) => ({
		id: `${edge.sourceId}->${edge.targetId}`,
		source: edge.sourceId,
		target: edge.targetId,
	}));

	return { nodes, edges };
}

// Maps React Flow nodes/edges + the Start seed text back into the backend graph (wire shape). Only Agent
// nodes carry the agent fields; the others contribute id + kind only. Empty strings are normalized to
// null so the wire matches the C# nullable fields (and the canvas never sends "" for an unset model).
function emptyToUndefined(value: string | undefined): string | undefined {
	const trimmed = value?.trim();
	return trimmed ? trimmed : undefined;
}

// Stable, order-independent serialization of a wire graph for equality checks. Positions are view-only (never on
// the wire), so two graphs are equal when their startText, node set, and edge set match regardless of array order.
// Nodes are keyed by id; edges by source→target. Used to decide whether an opened workflow has unsaved edits
// (a dirty canvas executes its CURRENT graph instead of the persisted one).
function normalizeGraph(graph: PreviewWorkflowGraph): string {
	const nodes = [...graph.nodes]
		.sort((a, b) => a.id.localeCompare(b.id))
		.map((node) => ({
			id: node.id,
			kind: node.kind,
			label: node.label ?? null,
			instructions: node.instructions ?? null,
			model: node.model ?? null,
			modelProfile: node.modelProfile ?? null,
			reasoningEffort: node.reasoningEffort ?? null,
		}));
	const edges = [...graph.edges]
		.map((edge) => ({ sourceId: edge.sourceId, targetId: edge.targetId }))
		.sort((a, b) => `${a.sourceId}->${a.targetId}`.localeCompare(`${b.sourceId}->${b.targetId}`));
	return JSON.stringify({ startText: graph.startText, nodes, edges });
}

// True when two wire graphs are structurally equal (order-independent, positions ignored).
export function graphsEqual(a: PreviewWorkflowGraph, b: PreviewWorkflowGraph): boolean {
	return normalizeGraph(a) === normalizeGraph(b);
}

export function canvasToGraph(nodes: PreviewCanvasNode[], edges: PreviewCanvasEdge[], startText: string): PreviewWorkflowGraph {
	const graphNodes: PreviewWorkflowGraphNode[] = nodes.map((node) => {
		const base: PreviewWorkflowGraphNode = { id: node.id, kind: node.data.kind };
		if (node.data.kind !== "Agent") {
			return base;
		}
		return {
			...base,
			label: emptyToUndefined(node.data.label),
			instructions: emptyToUndefined(node.data.instructions),
			model: emptyToUndefined(node.data.model),
			modelProfile: emptyToUndefined(node.data.modelProfile),
			reasoningEffort: emptyToUndefined(node.data.reasoningEffort),
		};
	});

	return {
		startText,
		nodes: graphNodes,
		edges: edges.map((edge) => ({ sourceId: edge.source, targetId: edge.target })),
	};
}
