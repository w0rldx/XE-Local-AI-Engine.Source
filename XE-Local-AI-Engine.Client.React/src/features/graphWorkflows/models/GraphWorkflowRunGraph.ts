// The run view's canvas: the DEFINITION's graph, drawn as it was authored, with each node run's state attached.
//
// `GET runs/{runId}` carries no graph — every node of the pinned graph is materialized as a node run at run start, so
// the rows are the run's truth while the shape comes from `GET definitions/{definitionId}`. The two are only the same
// graph while `run.graphHash === definition.graphHash`; when they differ (someone saved the definition after the run
// started) the view falls back to NODES ONLY, laid out from the rows, and says so. This is an orchestrator ruling: no
// backend field is added, and drawing the current definition's edges over an older run would be a lie about routing.

import {
	defaultNodeData,
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

export interface GraphWorkflowRunCanvasSource {
	readonly run: GraphWorkflowRunSummaryResponse | undefined;
	readonly nodeRuns: readonly GraphWorkflowNodeRunSummaryResponse[];
	/** The definition the run was started from, with its own hash. Absent while the definition query is still loading. */
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
	/** The run's pinned graph is not the definition on screen: edges are unknown, so none are drawn. */
	readonly graphMismatch: boolean;
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

/** The run's node runs and, when the hashes agree, the definition's shape — as one read-only React Flow graph. */
export function toGraphWorkflowRunCanvas(source: GraphWorkflowRunCanvasSource): GraphWorkflowRunCanvas {
	const nodeRuns = source.nodeRuns;
	const runStates = new Map(nodeRuns.map((nodeRun) => [nodeRun.nodeKey ?? "", runStateOf(nodeRun)]));
	const graphHash = source.run?.graphHash ?? "";
	const definition = source.definitionGraph;
	const matches = definition?.graph !== undefined && graphHash.length > 0 && (definition.graphHash ?? "") === graphHash;

	if (matches) {
		const canvas = graphToCanvas(definition.graph);
		const structuralKey = buildStructuralKey(
			canvas.nodes.map((node) => node.id),
			canvas.edges.map((edge) => edge.id),
			graphHash,
		);
		if (canvas.nodes.length > GRAPH_WORKFLOW_MAX_RENDERED_NODES) {
			return overCap(structuralKey, canvas.nodes.length, false);
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
			graphMismatch: false,
		};
	}

	// Nodes only. A node run whose key is in no definition is still drawn here — the rows ARE the run, and hiding one
	// would understate what happened.
	const structuralKey = buildStructuralKey(
		nodeRuns.map((nodeRun) => nodeRun.nodeKey ?? ""),
		[],
		graphHash,
	);
	if (nodeRuns.length > GRAPH_WORKFLOW_MAX_RENDERED_NODES) {
		return overCap(structuralKey, nodeRuns.length, definition !== undefined);
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
		// Only a run whose definition we HAVE and whose hash disagrees is a mismatch; a definition that has not loaded
		// yet is simply not drawn, and warning about it would blame the operator for a pending query.
		graphMismatch: definition !== undefined,
		isOverCap: false,
	};
}

function buildStructuralKey(nodeKeys: readonly string[], edgeKeys: readonly string[], graphHash: string): string {
	return `${nodeKeys.toSorted().join(",")}|${edgeKeys.toSorted().join(",")}|${graphHash}`;
}

/** Past the cap nothing is laid out: the alert is the render, and the node table is the path through the run. */
function overCap(structuralKey: string, nodeCount: number, graphMismatch: boolean): GraphWorkflowRunCanvas {
	return { nodes: [], edges: [], structuralKey, nodeCount, isOverCap: true, graphMismatch };
}
