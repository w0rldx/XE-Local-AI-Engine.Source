// The graph editor's whole mutable state, in one hook. React Flow is CONTROLLED from here, so the canvas only renders
// and the config panels mutate through the same handles the canvas does — one source of truth for "what is on screen".
//
// Everything derived (`graph`, `issues`, `isDirty`) is memoised off `nodes`/`edges`, so a panel never has to remember
// to re-run the validator after an edit. Nothing here touches the network: the page owns the queries.

import {
	applyEdgeChanges,
	applyNodeChanges,
	type OnConnect,
	type OnEdgesChange,
	type OnNodesChange,
	type XYPosition,
} from "@xyflow/react";
import { useCallback, useMemo, useState } from "react";

import {
	canvasToGraph,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasEdgeCondition,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowEdgeData,
	defaultNodeData,
	graphToCanvas,
	graphWorkflowNodeTypeByKind,
	graphWorkflowsEqual,
	mintEdgeKey,
	mintNodeKey,
	renameNodeKey,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	GRAPH_WORKFLOW_MAX_NODES,
	type GraphWorkflowGraph,
	type GraphWorkflowNodeKind,
	asGraphWorkflowDecisionKind,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	type GraphWorkflowGraphIssue,
	validateGraphWorkflowGraph,
} from "@/features/graphWorkflows/models/GraphWorkflowValidation";

/**
 * A gesture the editor declined, for the caller to render. Only two exist, and both are rules the operator would
 * otherwise only meet at save time: a second unconditional edge over one pair, and the node cap.
 *
 * `seq` is what makes the same refusal twice in a row a NEW refusal — without it, dismissing the notice and repeating
 * the gesture would set identical state, React would skip the re-render, and the second refusal would be silent.
 */
export interface GraphWorkflowEditorRefusal {
	readonly rule: "parallelEdgesBothUnconditional" | "tooManyNodes";
	readonly seq: number;
}

export type GraphWorkflowRenameOutcome = "ok" | "collision" | "invalid";

export interface GraphWorkflowEditorState {
	readonly nodes: readonly GraphWorkflowCanvasNode[];
	readonly edges: readonly GraphWorkflowCanvasEdge[];
	/** `canvasToGraph(nodes, edges).graph`, memoised — what a save would send. */
	readonly graph: GraphWorkflowGraph;
	/**
	 * What the LOADED graph carried that the canvas cannot represent, plus the conversion's own issues, plus the
	 * structural rules. Client-side only; the server's own errors are merged in by the page.
	 */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	readonly isDirty: boolean;
	readonly canAddNode: boolean;
	/** The last declined gesture, or `undefined`. Cleared by {@link GraphWorkflowEditorState.dismissRefusal}. */
	readonly lastRefusal: GraphWorkflowEditorRefusal | undefined;
	readonly onNodesChange: OnNodesChange<GraphWorkflowCanvasNode>;
	readonly onEdgesChange: OnEdgesChange<GraphWorkflowCanvasEdge>;
	readonly onConnect: OnConnect;
	/** The minted key, or `undefined` when the graph is already at `GRAPH_WORKFLOW_MAX_NODES`. */
	readonly addNode: (kind: GraphWorkflowNodeKind, position?: XYPosition) => string | undefined;
	/** Patches one node's data. `kind` and `key` are never changed through this — a rename is {@link renameNode}. */
	readonly updateNodeData: (key: string, patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly renameNode: (from: string, to: string) => GraphWorkflowRenameOutcome;
	readonly updateEdgeData: (edgeId: string, patch: Partial<GraphWorkflowEdgeData>) => void;
	readonly removeNode: (key: string) => void;
	readonly removeEdge: (edgeId: string) => void;
	readonly autoArrange: () => void;
	/** Load a saved graph: new canvas, new dirty baseline. */
	readonly reset: (graph: GraphWorkflowGraph) => void;
	/** After a successful save: the baseline moves, the canvas does not. */
	readonly markSaved: (graph: GraphWorkflowGraph) => void;
	readonly dismissRefusal: () => void;
}

/**
 * What a new edge inherits from the handle it left. `sourceHandle` is authoring metadata the runtime ignores, so the
 * label and the condition are what actually route the branch — and prefilling them here is what stops an operator
 * having to know the Pause pre-flight rule exists.
 *
 * A Condition handle prefills NO path: the edge inherits its source node's `config.path` (ruling C2), and the client
 * cannot invent one.
 */
function connectionPrefill(
	sourceKind: GraphWorkflowNodeKind | undefined,
	handle: string | undefined,
): { readonly label?: string; readonly condition?: GraphWorkflowCanvasEdgeCondition } {
	if (handle === undefined || handle.length === 0) {
		return {};
	}
	if (sourceKind === "Condition" && (handle === "true" || handle === "false")) {
		return { label: handle, condition: { op: "Eq", value: handle } };
	}
	if (sourceKind === "Pause" && asGraphWorkflowDecisionKind(handle) !== undefined) {
		return { label: handle, condition: { path: "output.decision", op: "Eq", value: handle } };
	}
	return {};
}

/**
 * What the canvas could not represent about the graph it just loaded. `graphToCanvas` drops an unknown `op` and narrows
 * an unknown `kind`, so by the time `canvasToGraph` runs the evidence is gone and a save would silently rewrite the
 * branch. These are computed once, over the WIRE graph, and held until the next load or save.
 *
 * ponytail: inline filter over the full validator; swap for Lane B's `loadedGraphIssues` helper when that merges.
 */
function loadedGraphIssues(graph: GraphWorkflowGraph | undefined): readonly GraphWorkflowGraphIssue[] {
	return validateGraphWorkflowGraph(graph).filter(
		(issue) => issue.rule === "unknownConditionOperator" || issue.rule === "unknownNodeKind",
	);
}

/** Where a palette click drops a node when nothing said where. Staggered so a run of clicks does not stack one card. */
function nextFreePosition(count: number): XYPosition {
	return { x: 320, y: 80 + count * 60 };
}

export function useGraphWorkflowEditor(initial: GraphWorkflowGraph | undefined): GraphWorkflowEditorState {
	// Lazy initialisers, not an effect on `initial`: the page loads a definition by calling `reset`, so a re-render
	// with the same graph must never throw away the operator's in-progress edits.
	const [nodes, setNodes] = useState<readonly GraphWorkflowCanvasNode[]>(() => graphToCanvas(initial).nodes);
	const [edges, setEdges] = useState<readonly GraphWorkflowCanvasEdge[]>(() => graphToCanvas(initial).edges);
	const [baseline, setBaseline] = useState<GraphWorkflowGraph | undefined>(initial);
	const [lastRefusal, setLastRefusal] = useState<GraphWorkflowEditorRefusal | undefined>(undefined);
	const [loadedIssues, setLoadedIssues] = useState<readonly GraphWorkflowGraphIssue[]>(() => loadedGraphIssues(initial));

	const refuse = useCallback((rule: GraphWorkflowEditorRefusal["rule"]) => {
		setLastRefusal((previous) => ({ rule, seq: (previous?.seq ?? 0) + 1 }));
	}, []);

	const conversion = useMemo(() => canvasToGraph(nodes, edges), [nodes, edges]);
	// The loaded-graph issues survive every edit: moving a node does not make an operator's unrepresentable `op` token
	// representable, and Save has to stay refused until the graph is loaded or saved again.
	const issues = useMemo(
		() => [...loadedIssues, ...conversion.issues, ...validateGraphWorkflowGraph(conversion.graph)],
		[conversion.graph, conversion.issues, loadedIssues],
	);

	const onNodesChange = useCallback<OnNodesChange<GraphWorkflowCanvasNode>>((changes) => {
		setNodes((current) => applyNodeChanges(changes, [...current]));
	}, []);

	const onEdgesChange = useCallback<OnEdgesChange<GraphWorkflowCanvasEdge>>((changes) => {
		setEdges((current) => applyEdgeChanges(changes, [...current]));
	}, []);

	const onConnect = useCallback<OnConnect>(
		(connection) => {
			const source = connection.source;
			const target = connection.target;
			if (source.length === 0 || target.length === 0) {
				return;
			}
			const handle = connection.sourceHandle ?? undefined;
			const prefill = connectionPrefill(nodes.find((node) => node.id === source)?.data.kind, handle);
			// Parallel edges over one pair are legal — a Pause routing Approve and Reject to the same End is the natural
			// shape — but at most ONE of them may be unconditional, because a second one is a branch that can never be
			// told from the first. Everything else connects.
			if (
				prefill.condition === undefined &&
				edges.some((edge) => edge.source === source && edge.target === target && edge.data?.condition === undefined)
			) {
				refuse("parallelEdgesBothUnconditional");
				return;
			}
			const key = mintEdgeKey([...nodes.map((node) => node.id), ...edges.map((edge) => edge.id)]);
			setEdges([
				...edges,
				{
					id: key,
					source,
					target,
					...(handle === undefined ? {} : { sourceHandle: handle }),
					// React Flow renders the TOP-LEVEL label natively, so a branch is readable without opening a panel;
					// `data.label` is what the drawer edits and what `canvasToGraph` prefers. Both are written.
					...(prefill.label === undefined ? {} : { label: prefill.label }),
					data: {
						...(prefill.label === undefined ? {} : { label: prefill.label }),
						...(prefill.condition === undefined ? {} : { condition: prefill.condition }),
					},
				},
			]);
		},
		[edges, nodes, refuse],
	);

	const addNode = useCallback(
		(kind: GraphWorkflowNodeKind, position?: XYPosition): string | undefined => {
			if (nodes.length >= GRAPH_WORKFLOW_MAX_NODES) {
				refuse("tooManyNodes");
				return undefined;
			}
			const key = mintNodeKey(kind, [...nodes.map((node) => node.id), ...edges.map((edge) => edge.id)]);
			setNodes([
				...nodes,
				{
					id: key,
					type: graphWorkflowNodeTypeByKind[kind],
					position: position ?? nextFreePosition(nodes.length),
					data: defaultNodeData(kind, key),
				},
			]);
			return key;
		},
		[edges, nodes, refuse],
	);

	const updateNodeData = useCallback((key: string, patch: Partial<GraphWorkflowCanvasNodeData>) => {
		setNodes((current) =>
			current.map((node) =>
				node.id === key
					? // `kind` and `key` are re-pinned AFTER the patch: a panel that spreads a whole data object in must not
						// be able to turn an Agent card into an End one, and a rename has to go through the cascade in `renameNode`.
						{ ...node, data: { ...node.data, ...patch, kind: node.data.kind, key: node.data.key } as GraphWorkflowCanvasNodeData }
					: node,
			),
		);
	}, []);

	const renameNode = useCallback(
		(from: string, to: string): GraphWorkflowRenameOutcome => {
			const result = renameNodeKey([...nodes], [...edges], from, to);
			if ("error" in result) {
				return result.error;
			}
			setNodes(result.nodes);
			setEdges(result.edges);
			return "ok";
		},
		[edges, nodes],
	);

	const updateEdgeData = useCallback((edgeId: string, patch: Partial<GraphWorkflowEdgeData>) => {
		setEdges((current) =>
			current.map((edge) => {
				if (edge.id !== edgeId) {
					return edge;
				}
				const data: GraphWorkflowEdgeData = { ...edge.data, ...patch };
				const label = data.label ?? "";
				return { ...edge, data, ...(label.length > 0 ? { label } : { label: undefined }) };
			}),
		);
	}, []);

	const removeNode = useCallback((key: string) => {
		setNodes((current) => current.filter((node) => node.id !== key));
		// The incident edges go with it: leaving them behind would produce `unknownEdgeEndpoint` on every later validate.
		// Destructured rather than `edge.target`: `CheckEventCurrentTargetInUpdaters` flags any `.target` read inside a
		// functional updater, and React Flow's edge endpoint happens to share the name of the DOM event property it guards.
		setEdges((current) => current.filter(({ source, target }) => source !== key && target !== key));
	}, []);

	const removeEdge = useCallback((edgeId: string) => {
		setEdges((current) => current.filter((edge) => edge.id !== edgeId));
	}, []);

	const autoArrange = useCallback(() => {
		setNodes((current) => {
			const layout = layoutGraphWorkflow(
				current.map((node) => ({ key: node.id })),
				edges.map((edge) => ({ from: edge.source, to: edge.target })),
			);
			// Laid out from the CANVAS, not from a wire round trip: re-reading the graph would re-derive every card's data
			// from its config and lose whatever half-typed JSON the operator has in a text field.
			return current.map((node) => {
				const placed = layout.positions.get(node.id);
				return placed === undefined ? node : { ...node, position: { x: placed.x, y: placed.y } };
			});
		});
	}, [edges]);

	const reset = useCallback((graph: GraphWorkflowGraph) => {
		const canvas = graphToCanvas(graph);
		setNodes(canvas.nodes);
		setEdges(canvas.edges);
		setBaseline(graph);
		setLoadedIssues(loadedGraphIssues(graph));
		setLastRefusal(undefined);
	}, []);

	const markSaved = useCallback((graph: GraphWorkflowGraph) => {
		setBaseline(graph);
		// The save just wrote what the canvas holds, so whatever the server stored that the canvas could not represent is
		// gone with it.
		setLoadedIssues([]);
	}, []);

	const dismissRefusal = useCallback(() => {
		setLastRefusal(undefined);
	}, []);

	return {
		nodes,
		edges,
		graph: conversion.graph,
		issues,
		isDirty: !graphWorkflowsEqual(conversion.graph, baseline),
		canAddNode: nodes.length < GRAPH_WORKFLOW_MAX_NODES,
		lastRefusal,
		onNodesChange,
		onEdgesChange,
		onConnect,
		addNode,
		updateNodeData,
		renameNode,
		updateEdgeData,
		removeNode,
		removeEdge,
		autoArrange,
		reset,
		markSaved,
		dismissRefusal,
	};
}
