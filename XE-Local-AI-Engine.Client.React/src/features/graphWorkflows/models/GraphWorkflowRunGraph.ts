// The run view's canvas: the graph the run PINNED at start, with each node run's state attached.
//
// `GET runs/{runId}` carries that pinned graph (F5-1), so the shape on screen is the shape this run actually routed
// on, whatever the definition says today. The node runs stay the run's truth — every node of the pinned graph is
// materialized at run start — and they are overlaid on it. A definition edited since is then only worth SAYING
// (`graphNotice: "definitionChanged"`), never worth degrading the drawing for.
//
// The pre-F5-1 path is still here for a response that carries no graph: the definition's current graph while
// `run.graphHash === definition.graphHash`, and NODES ONLY, laid out from the rows, when the two disagree
// (`graphNotice: "nodesOnly"`) — drawing today's edges over an older run would be a lie about routing.

import {
	defaultNodeData,
	type GraphWorkflowCanvas,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasRunState,
	graphToCanvas,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	asGraphWorkflowDecisionKind,
	GRAPH_WORKFLOW_MAX_RENDERED_NODES,
	type GraphWorkflowGraph,
	type GraphWorkflowNodeRunSummaryResponse,
	type GraphWorkflowRunSummaryResponse,
	narrowGraphWorkflowFailureClass,
	narrowGraphWorkflowNodeKind,
	narrowGraphWorkflowNodeRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/**
 * What the run view knows about a graph that is no longer the definition on screen.
 *
 * `definitionChanged` — informational only: the pinned graph IS drawn, the definition has simply moved on.
 * `nodesOnly` — no pinned graph came back and the hashes disagree, so the edges are unknown and none are drawn.
 */
export type GraphWorkflowRunGraphNotice = "definitionChanged" | "nodesOnly";

export interface GraphWorkflowRunCanvasSource {
	readonly run: GraphWorkflowRunSummaryResponse | undefined;
	readonly nodeRuns: readonly GraphWorkflowNodeRunSummaryResponse[];
	/**
	 * The run's PINNED graph, off `GET runs/{runId}`. Preferred over the definition whenever it is there: it is the
	 * document this run routed on. Optional because a response older than F5-1 carries none.
	 */
	readonly runGraph?: GraphWorkflowGraph;
	/** The definition as it stands NOW, with its own hash. Absent while the definition query is still loading. */
	readonly definitionGraph?: {
		readonly graph: GraphWorkflowGraph | undefined;
		readonly graphHash?: string | null;
	};
}

export interface GraphWorkflowRunCanvas {
	readonly nodes: GraphWorkflowCanvasNode[];
	readonly edges: GraphWorkflowCanvasEdge[];
	/** Node keys, edge keys and the graph hash — everything the LAYOUT depends on and nothing that ticks, so a status
	 * change never re-frames the viewport. */
	readonly structuralKey: string;
	readonly nodeCount: number;
	readonly isOverCap: boolean;
	/** Undefined when the definition on screen is the graph that ran; otherwise what the view has to say about it. */
	readonly graphNotice?: GraphWorkflowRunGraphNotice;
}

/**
 * `graphToCanvas` parses the whole document and, when a node carries no stored position, LAYS IT OUT — and the run
 * hub invalidates the run detail on every event, so a single node transition would otherwise re-parse and re-rank up
 * to a MiB of graph just to redraw one badge. Its result is a pure function of the graph, and TanStack's structural
 * sharing hands back the SAME `graph` object while its JSON is unchanged, so object identity is a sound key.
 *
 * A `WeakMap`, so an entry dies with the graph that keyed it and nothing has to be evicted. Callers must treat the
 * result as FROZEN and copy what they annotate, as `toGraphWorkflowRunCanvas` does with every node and edge it
 * returns — the run view and the Pause panel both read this same conversion.
 */
const canvasByGraph = new WeakMap<GraphWorkflowGraph, GraphWorkflowCanvas>();

export function cachedGraphToCanvas(graph: GraphWorkflowGraph): GraphWorkflowCanvas {
	const cached = canvasByGraph.get(graph);
	if (cached !== undefined) {
		return cached;
	}
	const canvas = graphToCanvas(graph);
	canvasByGraph.set(graph, canvas);
	return canvas;
}

function runStateOf(nodeRun: GraphWorkflowNodeRunSummaryResponse): GraphWorkflowCanvasRunState {
	const pending = asGraphWorkflowDecisionKind(nodeRun.pendingDecisionKind);
	return {
		status: narrowGraphWorkflowNodeRunStatus(nodeRun.status),
		attempt: nodeRun.attempt ?? 1,
		failureClass: narrowGraphWorkflowFailureClass(nodeRun.failureClass),
		...(pending ? { pendingDecisionKind: pending } : {}),
	};
}

/** The run's node runs over the graph it ran on — pinned when the response carries one — as one read-only React Flow graph. */
export function toGraphWorkflowRunCanvas(source: GraphWorkflowRunCanvasSource): GraphWorkflowRunCanvas {
	const nodeRuns = source.nodeRuns;
	const runStates = new Map(nodeRuns.map((nodeRun) => [nodeRun.nodeKey ?? "", runStateOf(nodeRun)]));
	const graphHash = source.run?.graphHash ?? "";
	const definition = source.definitionGraph;
	const matches = definition?.graph !== undefined && graphHash.length > 0 && (definition.graphHash ?? "") === graphHash;
	// Only a definition we HAVE and whose hash disagrees has drifted; a definition that has not loaded yet is simply
	// not compared, and warning about it would blame the operator for a pending query.
	const drifted = definition?.graph !== undefined && !matches;
	// A stored node with no position is laid out by `graphToCanvas` itself, so a pinned graph authored before the
	// editor saved positions still draws.
	const graph = source.runGraph ?? (matches ? definition?.graph : undefined);

	if (graph !== undefined) {
		const notice = drifted ? ("definitionChanged" as const) : undefined;
		const canvas = cachedGraphToCanvas(graph);
		const structuralKey = buildStructuralKey(
			canvas.nodes.map((node) => node.id),
			canvas.edges.map((edge) => edge.id),
			graphHash,
		);
		if (canvas.nodes.length > GRAPH_WORKFLOW_MAX_RENDERED_NODES) {
			return overCap(structuralKey, canvas.nodes.length, notice);
		}
		const nodes = canvas.nodes.map((node) => {
			const runState = runStates.get(node.id);
			return {
				...node,
				deletable: false,
				draggable: false,
				connectable: false,
				data: runState === undefined ? node.data : { ...node.data, runState },
			};
		});
		const edges = canvas.edges.map((edge) => ({ ...edge, deletable: false, selectable: false, focusable: false }));
		return {
			nodes,
			edges,
			structuralKey,
			nodeCount: nodes.length,
			isOverCap: false,
			...(notice ? { graphNotice: notice } : {}),
		};
	}

	// Nodes only. A node run whose key is in no definition is still drawn here — the rows ARE the run, and hiding one
	// would understate what happened.
	const notice = definition !== undefined ? ("nodesOnly" as const) : undefined;
	const structuralKey = buildStructuralKey(
		nodeRuns.map((nodeRun) => nodeRun.nodeKey ?? ""),
		[],
		graphHash,
	);
	if (nodeRuns.length > GRAPH_WORKFLOW_MAX_RENDERED_NODES) {
		return overCap(structuralKey, nodeRuns.length, notice);
	}
	const layout = layoutGraphWorkflow(
		nodeRuns.map((nodeRun) => ({ key: nodeRun.nodeKey ?? "" })),
		[],
	);
	const nodes = nodeRuns.map((nodeRun): GraphWorkflowCanvasNode => {
		const key = nodeRun.nodeKey ?? "";
		const kind = narrowGraphWorkflowNodeKind(nodeRun.kind);
		const placed = layout.positions.get(key);
		return {
			id: key,
			type: graphWorkflowNodeTypeByKind[kind],
			position: { x: placed?.x ?? 0, y: placed?.y ?? 0 },
			deletable: false,
			draggable: false,
			connectable: false,
			data: { ...defaultNodeData(kind, key), label: key, runState: runStateOf(nodeRun) },
		};
	});
	return {
		nodes,
		edges: [],
		structuralKey,
		nodeCount: nodes.length,
		isOverCap: false,
		...(notice ? { graphNotice: notice } : {}),
	};
}

function buildStructuralKey(nodeKeys: readonly string[], edgeKeys: readonly string[], graphHash: string): string {
	return `${nodeKeys.toSorted().join(",")}|${edgeKeys.toSorted().join(",")}|${graphHash}`;
}

/** Past the cap nothing is laid out: the alert is the render, and the node table is the path through the run. */
function overCap(
	structuralKey: string,
	nodeCount: number,
	notice: GraphWorkflowRunGraphNotice | undefined,
): GraphWorkflowRunCanvas {
	return { nodes: [], edges: [], structuralKey, nodeCount, isOverCap: true, ...(notice ? { graphNotice: notice } : {}) };
}
