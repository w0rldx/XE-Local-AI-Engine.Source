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
	type DevWorkflowNodeStatus,
	type DevWorkflowNodeType,
	type DevWorkflowRunResponse,
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
	readonly status: DevWorkflowNodeStatus;
	readonly queueReason?: string;
	readonly queuedAtUtc?: number;
	readonly startedAtUtc?: number;
	readonly attempt: number;
	readonly maxAttempts: number;
	readonly agentDisplayName?: string;
	readonly modelLabel?: string;
	readonly isMaterialized: boolean;
	readonly materializedFromNodeKey?: string;
	readonly materializationIndex?: number;
	readonly hasStaleInputs: boolean;
	readonly waitingOnNodeKeys?: readonly string[];
	readonly developmentProjectId?: string;
	readonly developmentTaskId?: string;
}

/** Y6: a pure client visual with no server id and no status — it exists because a DAG with no visible entry point
 * reads as truncated. Never selectable, so it can never become a drill-down target. */
export interface DevWorkflowAnchorNodeData extends Record<string, unknown> {
	readonly anchor: "start" | "end";
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
	const nodeIds = (run?.nodes ?? []).map((node) => node.id ?? "").join(",");
	const edgePairs = (run?.graph?.edges ?? []).map((edge) => `${edge.from ?? ""}>${edge.to ?? ""}`).toSorted();
	return `${nodeIds}|${edgePairs.join(",")}`;
}

export function toDevWorkflowCanvasGraph(run: DevWorkflowRunResponse | undefined): DevWorkflowCanvasGraph {
	const nodeRuns = run?.nodes ?? [];
	const structuralKey = devWorkflowGraphStructuralKey(run);
	if (nodeRuns.length > DEV_WORKFLOW_MAX_RENDERED_NODES) {
		// Nothing is laid out past the cap: the banner is the render, and the node table is the path through the run.
		return { nodes: [], edges: [], structuralKey, nodeRunCount: nodeRuns.length, isOverCap: true };
	}

	const nodeRunIdByKey = new Map(nodeRuns.map((node) => [node.nodeKey ?? "", node.id ?? ""]));
	// A1 is linear-only, so the only endpoint that can be missing today is a materialization TEMPLATE — it has no
	// node-run row until its children are materialized (Slice C). Dropping the edge is the honest render: there is no
	// node to draw it to.
	const joined = (run?.graph?.edges ?? []).flatMap((edge) => {
		const from = nodeRunIdByKey.get(edge.from ?? "");
		const to = nodeRunIdByKey.get(edge.to ?? "");
		return from && to ? [{ from, to }] : [];
	});

	const hasInbound = new Set(joined.map((edge) => edge.to));
	const hasOutbound = new Set(joined.map((edge) => edge.from));
	const anchors: { anchor: "start" | "end"; nodeRunId: string; nodeKey: string }[] = nodeRuns.flatMap((node) => {
		const id = node.id ?? "";
		const nodeKey = node.nodeKey ?? "";
		return [
			...(hasInbound.has(id) ? [] : [{ anchor: "start" as const, nodeRunId: id, nodeKey }]),
			...(hasOutbound.has(id) ? [] : [{ anchor: "end" as const, nodeRunId: id, nodeKey }]),
		];
	});

	// The anchors are ranked WITH the real nodes (Y6) rather than offset from them, so they cannot land on top of
	// anything: a start anchor is simply the only thing left with no inbound edge.
	const layoutNodes: DevWorkflowLayoutNode[] = [
		...nodeRuns.map((node) => ({
			id: node.id ?? "",
			nodeKey: node.nodeKey ?? "",
			materializedFromNodeKey: optional(node.materializedFromNodeKey),
			materializationIndex: optional(node.materializationIndex),
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
		...nodeRuns.map(
			(node): DevWorkflowCanvasNode => ({
				id: node.id ?? "",
				type: toDevWorkflowNodeType(node.nodeType),
				position: positionOf(node.id ?? ""),
				deletable: false,
				draggable: false,
				connectable: false,
				data: {
					nodeType: toDevWorkflowNodeType(node.nodeType),
					label: node.label ?? node.nodeKey ?? "",
					status: toDevWorkflowNodeStatus(node.status),
					queueReason: optional(node.queueReason),
					queuedAtUtc: optional(node.queuedAtUtc),
					startedAtUtc: optional(node.startedAtUtc),
					attempt: node.attempt ?? 1,
					maxAttempts: node.maxAttempts ?? 1,
					agentDisplayName: optional(node.agentDisplayName),
					modelLabel: optional(node.modelLabel),
					isMaterialized: node.isMaterialized ?? false,
					materializedFromNodeKey: optional(node.materializedFromNodeKey),
					materializationIndex: optional(node.materializationIndex),
					hasStaleInputs: node.hasStaleInputs ?? false,
					waitingOnNodeKeys: optional(node.waitingOnNodeKeys),
					developmentProjectId: optional(node.developmentProjectId),
					developmentTaskId: optional(node.developmentTaskId),
				},
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
				data: { anchor: entry.anchor },
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

	return { nodes, edges, structuralKey, nodeRunCount: nodeRuns.length, isOverCap: false };
}
