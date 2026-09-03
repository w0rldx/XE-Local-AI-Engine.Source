// Wire ↔ canvas mapping for the run graph (P4 §2.3.1). The analogue of `features/preview/models/PreviewCanvasModels`,
// and deliberately not a reuse of it (O3): the preview canvas is an editor whose nodes carry authoring fields and
// client-generated ids, while this one is a read-only render of a run whose node identity is the server's node-run id.
//
// Two id spaces meet here. The pinned graph's edges carry NODE KEYS (`from`/`to` name definition nodes); the node-run
// rows carry ids. The join is `nodes[].nodeKey`, and an edge whose endpoint has no node-run row is dropped.

import type { Edge, Node } from "@xyflow/react";
import { MarkerType } from "@xyflow/react";

import {
	type DevWorkflowLayoutEdge,
	type DevWorkflowLayoutNode,
	devWorkflowEdgeKey,
	layoutDevWorkflowGraph,
} from "@/features/devWorkflows/models/DevWorkflowLayout";
import {
	type DevWorkflowGraph,
	type DevWorkflowNodeStatus,
	type DevWorkflowNodeType,
	type DevWorkflowRunResponse,
	isDevWorkflowApplyToolMode,
	toDevWorkflowNodeStatus,
	toDevWorkflowNodeType,
} from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * Matches P2's `MaxNodeRunsPerRun` exactly, so the guard fires only if the server bound is raised without the client
 * following — which is precisely when a guard earns its keep (C41; a 300 cap over a 200 bound was dead code).
 * ponytail: flat render past the cap is refused outright rather than collapsed — grouping a materialized sibling group
 * into one expandable node is the v2 seam, and it needs a server-side group id.
 */
export const DEV_WORKFLOW_MAX_RENDERED_NODES = 200;

export interface DevWorkflowCanvasNodeData extends Record<string, unknown> {
	// Y6: seven members — Start/End are IMPLICIT (entry = no inbound edges, terminal = no outbound).
	readonly nodeType: DevWorkflowNodeType;
	readonly label: string;
	/**
	 * Absent on a DEFINITION render, where nothing has run. A definition node painted `Pending` would claim it is
	 * materialized and waiting on a dependency, which is a run's state and not a template's.
	 */
	readonly status?: DevWorkflowNodeStatus;
	readonly queueReason?: string;
	readonly queuedAtUtc?: number;
	readonly startedAtUtc?: number;
	readonly attempt: number;
	readonly maxAttempts: number;
	/** How many of `maxAttempts` an operator's Retry granted (FU2-3). Absent on a definition render. */
	readonly operatorRetries?: number;
	readonly agentDisplayName?: string;
	readonly modelLabel?: string;
	/**
	 * A Tool node that LANDS the approved patches rather than judging a checkout (R-C3). Read off the pinned graph's
	 * `toolMode`, which is on the definitions wire since FX-B L2 — before that an apply node was indistinguishable from
	 * a validation node until it had already written its report.
	 */
	readonly isApplyTool: boolean;
	readonly isMaterialized: boolean;
	readonly materializedFromNodeKey?: string;
	readonly materializationIndex?: number;
	/**
	 * How many CHILDREN this materialization produced — the denominator beside `materializationIndex`, which is the
	 * SERVER's index of the child this card belongs to and is already 1-based (C2). Read off the node-run row (Slice D):
	 * the runtime computes it per materialization GROUP, which is the number this card is asking for and the number a
	 * client count could not reach — a template subtree is cloned whole, so counting rows made every denominator N·k,
	 * and counting distinct indices still could not tell two decompositions of the same template apart.
	 *
	 * Absent on a definition render, where nothing has been materialized.
	 */
	readonly materializationCount?: number;
	readonly hasStaleInputs: boolean;
	readonly waitingOnNodeKeys?: readonly string[];
	readonly developmentProjectId?: string;
	readonly developmentTaskId?: string;
}

/** Y6: a pure client visual with no server id and no status — it exists because a DAG with no visible entry point
 * reads as truncated. Never selectable, so it can never become a drill-down target. */
export interface DevWorkflowAnchorNodeData extends Record<string, unknown> {
	readonly anchor: "start" | "end";
	/** The entry/terminal node this anchor was synthesised for — a graph with two entries needs two distinct Starts. */
	readonly nodeKey: string;
}

export type DevWorkflowCanvasNode = Node<DevWorkflowCanvasNodeData>;
export type DevWorkflowAnchorNode = Node<DevWorkflowAnchorNodeData>;

export interface DevWorkflowCanvasGraph {
	// Mutable arrays because React Flow's props are mutable; every one of them is freshly built here and never shared.
	readonly nodes: Node[];
	readonly edges: Edge[];
	/** Node ids plus sorted edge pairs — everything the layout depends on and nothing that ticks. Drives `fitView`. */
	readonly structuralKey: string;
	readonly nodeRunCount: number;
	readonly isOverCap: boolean;
}

export const DEV_WORKFLOW_ANCHOR_NODE_TYPE = "anchor";

function anchorId(anchor: "start" | "end", nodeRunId: string): string {
	return `dev-workflow-anchor-${anchor}-${nodeRunId}`;
}

/** Undefined rather than null, because the canvas data interface is optional-typed and `null` is only a wire shape. */
function optional<T>(value: T | null | undefined): T | undefined {
	return value ?? undefined;
}

export function devWorkflowGraphStructuralKey(run: DevWorkflowRunResponse | undefined): string {
	// Sorted, like the edge pairs: the key answers "is this the same graph", and a server reordering an unchanged node
	// set is not a different graph — re-framing the viewport over it would be a jump the operator did not ask for.
	const nodeIds = (run?.nodes ?? []).map((node) => node.id ?? "").toSorted();
	const edgePairs = (run?.graph?.edges ?? []).map((edge) => `${edge.from ?? ""}>${edge.to ?? ""}`).toSorted();
	return `${nodeIds.join(",")}|${edgePairs.join(",")}`;
}

/** One card's worth of input, from either data source: a node-run row or a definition's graph node. */
interface DevWorkflowCanvasEntry {
	readonly id: string;
	readonly nodeKey: string;
	/** The materialization this clone belongs to: the server's group id, or the template key when it sent none. */
	readonly materializationGroupKey?: string;
	readonly materializationIndex?: number;
	readonly data: DevWorkflowCanvasNodeData;
}

export function toDevWorkflowCanvasGraph(run: DevWorkflowRunResponse | undefined): DevWorkflowCanvasGraph {
	const nodeRuns = run?.nodes ?? [];
	const structuralKey = devWorkflowGraphStructuralKey(run);
	if (nodeRuns.length > DEV_WORKFLOW_MAX_RENDERED_NODES) {
		// Nothing is laid out past the cap: the banner is the render, and the node table is the path through the run.
		return { nodes: [], edges: [], structuralKey, nodeRunCount: nodeRuns.length, isOverCap: true };
	}

	// The node-run row carries no `toolMode` — it is authoring config, and P3 projects it on the GRAPH node only. The
	// pinned graph travels with the run, so the join is the node key, the same one the edges are drawn through.
	const applyToolKeys = new Set(
		(run?.graph?.nodes ?? []).filter((node) => isDevWorkflowApplyToolMode(node.toolMode)).map((node) => node.nodeKey ?? ""),
	);

	const entries: DevWorkflowCanvasEntry[] = nodeRuns.map((node) => ({
		id: node.id ?? "",
		nodeKey: node.nodeKey ?? "",
		materializationGroupKey: optional(node.materializationGroupId ?? node.materializedFromNodeKey),
		materializationIndex: optional(node.materializationIndex),
		data: {
			nodeType: toDevWorkflowNodeType(node.nodeType),
			label: node.label ?? node.nodeKey ?? "",
			status: toDevWorkflowNodeStatus(node.status),
			queueReason: optional(node.queueReason),
			queuedAtUtc: optional(node.queuedAtUtc),
			startedAtUtc: optional(node.startedAtUtc),
			attempt: node.attempt ?? 1,
			maxAttempts: node.maxAttempts ?? 1,
			operatorRetries: optional(node.operatorRetries),
			agentDisplayName: optional(node.agentDisplayName),
			modelLabel: optional(node.modelLabel),
			isApplyTool: applyToolKeys.has(node.nodeKey ?? ""),
			isMaterialized: node.isMaterialized ?? false,
			materializedFromNodeKey: optional(node.materializedFromNodeKey),
			materializationIndex: optional(node.materializationIndex),
			materializationCount: optional(node.materializationCount),
			hasStaleInputs: node.hasStaleInputs ?? false,
			waitingOnNodeKeys: optional(node.waitingOnNodeKeys),
			developmentProjectId: optional(node.developmentProjectId),
			developmentTaskId: optional(node.developmentTaskId),
		},
	}));

	return buildCanvasGraph(entries, run?.graph?.edges ?? [], structuralKey);
}

/**
 * The same canvas over a DEFINITION (P4 §4, slice B: one component, two data sources). A definition's nodes ARE its
 * key space, so identity is the node key and there is no row to join against — and no status, because nothing has run.
 */
export function toDevWorkflowDefinitionCanvasGraph(graph: DevWorkflowGraph | undefined): DevWorkflowCanvasGraph {
	const graphNodes = graph?.nodes ?? [];
	const structuralKey = `${graphNodes.map((node) => node.nodeKey ?? "").toSorted().join(",")}|${(graph?.edges ?? [])
		.map((edge) => `${edge.from ?? ""}>${edge.to ?? ""}`)
		.toSorted()
		.join(",")}`;
	if (graphNodes.length > DEV_WORKFLOW_MAX_RENDERED_NODES) {
		return { nodes: [], edges: [], structuralKey, nodeRunCount: graphNodes.length, isOverCap: true };
	}

	const entries: DevWorkflowCanvasEntry[] = graphNodes.map((node) => ({
		id: node.nodeKey ?? "",
		nodeKey: node.nodeKey ?? "",
		data: {
			nodeType: toDevWorkflowNodeType(node.nodeType),
			label: node.label ?? node.nodeKey ?? "",
			attempt: 1,
			maxAttempts: node.maxAttempts ?? 1,
			isApplyTool: isDevWorkflowApplyToolMode(node.toolMode),
			isMaterialized: false,
			hasStaleInputs: false,
		},
	}));

	return buildCanvasGraph(entries, graph?.edges ?? [], structuralKey);
}

function buildCanvasGraph(
	entries: readonly DevWorkflowCanvasEntry[],
	graphEdges: readonly { from?: string; to?: string }[],
	structuralKey: string,
): DevWorkflowCanvasGraph {
	const nodeRunIdByKey = new Map(entries.map((entry) => [entry.nodeKey, entry.id]));
	// A1 is linear-only, so the only endpoint that can be missing today is a materialization TEMPLATE — it has no
	// node-run row until its children are materialized (Slice C). Dropping the edge is the honest render: there is no
	// node to draw it to.
	const joined = graphEdges.flatMap((edge) => {
		const from = nodeRunIdByKey.get(edge.from ?? "");
		const to = nodeRunIdByKey.get(edge.to ?? "");
		return from && to ? [{ from, to }] : [];
	});

	// Degree is asked of the DEFINITION's edges, not of the joined ones: a node whose successor is a materialization
	// template has an outbound edge the canvas cannot draw yet, and capping it with an "End" anchor would claim the run
	// finishes there. It dangles instead, which is the truth until Slice C materializes the children.
	const hasInboundKey = new Set(graphEdges.map((edge) => edge.to ?? ""));
	const hasOutboundKey = new Set(graphEdges.map((edge) => edge.from ?? ""));
	const anchors: { anchor: "start" | "end"; nodeRunId: string; nodeKey: string }[] = entries.flatMap((node) => {
		const id = node.id;
		const nodeKey = node.nodeKey;
		return [
			...(hasInboundKey.has(nodeKey) ? [] : [{ anchor: "start" as const, nodeRunId: id, nodeKey }]),
			...(hasOutboundKey.has(nodeKey) ? [] : [{ anchor: "end" as const, nodeRunId: id, nodeKey }]),
		];
	});

	// The anchors are ranked WITH the real nodes (Y6) rather than offset from them, so they cannot land on top of
	// anything: a start anchor is simply the only thing left with no inbound edge.
	const layoutNodes: DevWorkflowLayoutNode[] = [
		...entries.map((node) => ({
			id: node.id,
			nodeKey: node.nodeKey,
			materializationGroupKey: node.materializationGroupKey,
			materializationIndex: node.materializationIndex,
		})),
		...anchors.map((entry) => ({ id: anchorId(entry.anchor, entry.nodeRunId), nodeKey: entry.nodeKey })),
	];
	const anchorEdges: DevWorkflowLayoutEdge[] = anchors.map((entry) =>
		entry.anchor === "start"
			? { from: anchorId("start", entry.nodeRunId), to: entry.nodeRunId }
			: { from: entry.nodeRunId, to: anchorId("end", entry.nodeRunId) },
	);
	const layout = layoutDevWorkflowGraph(layoutNodes, [...joined, ...anchorEdges]);
	const positionOf = (id: string) => {
		const position = layout.positions.get(id);
		return { x: position?.x ?? 0, y: position?.y ?? 0 };
	};

	const nodes: Node[] = [
		...entries.map(
			(node): DevWorkflowCanvasNode => ({
				id: node.id,
				type: node.data.nodeType,
				position: positionOf(node.id),
				deletable: false,
				draggable: false,
				connectable: false,
				data: node.data,
			}),
		),
		...anchors.map(
			(entry): DevWorkflowAnchorNode => ({
				id: anchorId(entry.anchor, entry.nodeRunId),
				type: DEV_WORKFLOW_ANCHOR_NODE_TYPE,
				position: positionOf(anchorId(entry.anchor, entry.nodeRunId)),
				deletable: false,
				draggable: false,
				connectable: false,
				selectable: false,
				focusable: false,
				data: { anchor: entry.anchor, nodeKey: entry.nodeKey },
			}),
		),
	];

	const edges: Edge[] = [...joined, ...anchorEdges].map((edge) => {
		const isBackEdge = layout.backEdgeKeys.has(devWorkflowEdgeKey(edge));
		return {
			id: devWorkflowEdgeKey(edge),
			source: edge.from,
			target: edge.to,
			deletable: false,
			selectable: false,
			focusable: false,
			animated: false,
			// X9 makes v1 definitions acyclic, so this styling is only ever seen if a server stops enforcing that.
			style: isBackEdge ? { strokeDasharray: "4 4" } : undefined,
			markerEnd: { type: MarkerType.ArrowClosed },
		};
	});

	return { nodes, edges, structuralKey, nodeRunCount: entries.length, isOverCap: false };
}
