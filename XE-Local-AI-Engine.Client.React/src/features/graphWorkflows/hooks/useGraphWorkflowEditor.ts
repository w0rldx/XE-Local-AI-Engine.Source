// The graph editor's whole mutable state, in one hook. React Flow is CONTROLLED from here, so the canvas only renders
// and the config panels mutate through the same handles the canvas does — one source of truth for "what is on screen".
//
// Everything derived (`graph`, `issues`, `isDirty`) is memoized off `nodes`/`edges`, so a panel never has to remember
// to re-run the validator after an edit. Nothing here touches the network: the page owns the queries.

import {
	applyEdgeChanges,
	applyNodeChanges,
	type OnConnect,
	type OnEdgesChange,
	type OnNodesChange,
	type XYPosition,
} from "@xyflow/react";
import { useCallback, useMemo, useRef, useState } from "react";

import {
	canvasToGraph,
	defaultNodeData,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasEdgeCondition,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowEdgeData,
	type GraphWorkflowGraphSettings,
	graphToCanvas,
	graphWorkflowNodeTypeByKind,
	graphWorkflowsEqual,
	mintEdgeKey,
	mintNodeKey,
	pauseContextEdges,
	renameNodeKey,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { designerProposalCanvas } from "@/features/graphWorkflows/models/GraphWorkflowDesignerModels";
import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	asGraphWorkflowDecisionKind,
	GRAPH_WORKFLOW_MAX_NODES,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowDesignerChangeResponse,
	type GraphWorkflowGraph,
	type GraphWorkflowNodeKind,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	type GraphWorkflowGraphIssue,
	loadedGraphIssues,
	validateGraphWorkflowGraph,
} from "@/features/graphWorkflows/models/GraphWorkflowValidation";

/**
 * What the editor has to say about the last gesture, for the caller to render. Two are refusals — rules the operator
 * would otherwise only meet at save time: a second unconditional edge over one pair, and the node cap. The third
 * reports what wiring a Pause added, because an edge appearing on the canvas unannounced is worse than no affordance.
 *
 * `seq` is what makes the same notice twice in a row a NEW notice — without it, dismissing it and repeating the
 * gesture would set identical state, React would skip the re-render, and the second one would be silent.
 */
export interface GraphWorkflowEditorNotice {
	readonly rule: "parallelEdgesBothUnconditional" | "tooManyNodes" | "pauseContextEdgeAdded";
	readonly seq: number;
}

export type GraphWorkflowRenameOutcome = "ok" | "collision" | "invalid";

/**
 * `generating`: a designer turn is in flight, so the working graph is frozen. `reviewing`: the canvas shows the proposal
 * the turn returned, inspectable but not editable, until it is accepted or rejected. Every mutator is a no-op outside
 * `editing`, so a stray panel or keyboard gesture cannot change the graph the proposal was made against.
 */
export type GraphWorkflowEditorMode = "editing" | "generating" | "reviewing";

/** What a designer turn proposed, as {@link GraphWorkflowEditorState.showProposal} takes it. */
export interface GraphWorkflowProposalInput {
	readonly proposal: GraphWorkflowGraph;
	readonly changes: readonly GraphWorkflowDesignerChangeResponse[];
	readonly valid: boolean;
	/** The server's validation errors for the proposal, as text; empty when it is valid. */
	readonly errors: readonly string[];
	/** The working graph the request was sent with. Accept is refused once the canvas no longer holds it. */
	readonly basis: GraphWorkflowGraph;
}

export interface GraphWorkflowEditorReview extends GraphWorkflowProposalInput {
	/** The proposal as the canvas draws it: diff-tagged, with removed elements ghosted from the working graph. */
	readonly canvas: ReturnType<typeof designerProposalCanvas>;
}

interface GraphWorkflowCanvasSnapshot {
	readonly nodes: readonly GraphWorkflowCanvasNode[];
	readonly edges: readonly GraphWorkflowCanvasEdge[];
	readonly settings: GraphWorkflowGraphSettings;
	readonly loadedIssues: readonly GraphWorkflowGraphIssue[];
}

export interface GraphWorkflowEditorState {
	readonly nodes: readonly GraphWorkflowCanvasNode[];
	readonly edges: readonly GraphWorkflowCanvasEdge[];
	/** The graph-level `kind` and `chat` block. Part of the graph document, so an edit here dirties the canvas. */
	readonly settings: GraphWorkflowGraphSettings;
	/** `canvasToGraph(nodes, edges, settings).graph`, memoized — what a save would send. */
	readonly graph: GraphWorkflowGraph;
	/**
	 * What the LOADED graph carried that the canvas cannot round-trip, plus the conversion's own issues, plus the
	 * structural rules. Client-side only; the server's own errors are merged in by the page.
	 */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	readonly isDirty: boolean;
	readonly canAddNode: boolean;
	/** What the editor said about the last gesture, or `undefined`. Cleared by {@link GraphWorkflowEditorState.dismissNotice}. */
	readonly lastNotice: GraphWorkflowEditorNotice | undefined;
	readonly onNodesChange: OnNodesChange<GraphWorkflowCanvasNode>;
	readonly onEdgesChange: OnEdgesChange<GraphWorkflowCanvasEdge>;
	readonly onConnect: OnConnect;
	/** The minted key, or `undefined` when the graph is already at `GRAPH_WORKFLOW_MAX_NODES`. */
	readonly addNode: (kind: GraphWorkflowNodeKind, position?: XYPosition) => string | undefined;
	/** Patches one node's data. `kind` and `key` are never changed through this — a rename is {@link renameNode}. */
	readonly updateNodeData: (key: string, patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly renameNode: (from: string, to: string) => GraphWorkflowRenameOutcome;
	readonly updateEdgeData: (edgeId: string, patch: Partial<GraphWorkflowEdgeData>) => void;
	readonly updateSettings: (next: GraphWorkflowGraphSettings) => void;
	readonly removeNode: (key: string) => void;
	readonly removeEdge: (edgeId: string) => void;
	readonly autoArrange: () => void;
	/** Load a saved graph: new canvas, new dirty baseline. */
	readonly reset: (graph: GraphWorkflowGraph) => void;
	/** After a successful save: the baseline moves, the canvas does not. */
	readonly markSaved: (graph: GraphWorkflowGraph) => void;
	readonly dismissNotice: () => void;
	readonly mode: GraphWorkflowEditorMode;
	readonly review: GraphWorkflowEditorReview | undefined;
	/** The working graph changed since the proposal's request was sent, so Accept would apply it to another graph. */
	readonly isReviewStale: boolean;
	/** One step back to the canvas as it was before the last Accept, until the next edit or save. */
	readonly canUndoAccept: boolean;
	/**
	 * Bumped when the canvas swaps in a whole other graph: a proposal shown, accepted or undone. The canvas re-frames
	 * the viewport on it; an ordinary edit never moves it.
	 */
	readonly viewEpoch: number;
	/** `editing` → `generating`. `false` when the editor was not editing. */
	readonly beginGenerating: () => boolean;
	/** `generating` → `editing`, graph untouched: a cancelled or failed turn, or one that answered without a proposal. */
	readonly endGenerating: () => void;
	/** `generating` → `reviewing`. */
	readonly showProposal: (input: GraphWorkflowProposalInput) => void;
	/** `reviewing` → `editing`, graph untouched. */
	readonly rejectProposal: () => void;
	/**
	 * `reviewing` → `editing` with the proposal as the working canvas. The saved baseline stays, so the editor is dirty.
	 * `false` (nothing changes) for an invalid or stale proposal.
	 */
	readonly acceptProposal: () => boolean;
	readonly undoAccept: () => void;
}

/**
 * What a new edge inherits from the handle it left. `sourceHandle` is authoring metadata the runtime ignores, so the
 * label and the condition are what actually route the branch — and prefilling them here is what stops an operator
 * having to know the Pause pre-flight rule exists.
 *
 * A Condition handle prefills NO path: the edge inherits its source node's `config.path`, and the client
 * cannot invent one.
 *
 * A condition `value` is canvas text, and that text is JSON (`conditionValueText`): the boolean branch is `true` bare,
 * the decision is `"Approve"` QUOTED. Writing the decision unquoted would save the same string but render differently
 * from the identical edge reopened later.
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
		return { label: handle, condition: { path: "output.decision", op: "Eq", value: JSON.stringify(handle) } };
	}
	return {};
}

/**
 * The decisions a Pause node stops offering under this patch. Empty for every other edit.
 *
 * `patch` is a partial of a UNION, so `allowedDecisions` is only reachable through the index signature the node base
 * carries; the narrowing back to real decision kinds is the model's.
 */
function droppedDecisions(
	previous: GraphWorkflowCanvasNodeData | undefined,
	patch: Partial<GraphWorkflowCanvasNodeData>,
): readonly GraphWorkflowDecisionKind[] {
	const next = patch["allowedDecisions"];
	if (previous?.kind !== "Pause" || !Array.isArray(next)) {
		return [];
	}
	const kept = toGraphWorkflowDecisionKinds(next.filter((entry): entry is string => typeof entry === "string"));
	return previous.allowedDecisions.filter((decision) => !kept.includes(decision));
}

/** Which decision an out-edge of a Pause routes: its handle, or the condition value when no handle was stored. */
function edgeDecision(edge: GraphWorkflowCanvasEdge): string {
	return edge.sourceHandle ?? edge.data?.condition?.value ?? "";
}

/**
 * Where a palette click drops a node when nothing said where: the first staggered slot no node already occupies.
 * Staggering by the node COUNT would reuse a slot the moment anything was deleted, dropping the new card on top of one.
 */
function nextFreePosition(nodes: readonly GraphWorkflowCanvasNode[]): XYPosition {
	for (let index = 0; ; index += 1) {
		const candidate = { x: 320, y: 80 + index * 60 };
		if (!nodes.some((node) => node.position.x === candidate.x && node.position.y === candidate.y)) {
			return candidate;
		}
	}
}

export function useGraphWorkflowEditor(initial: GraphWorkflowGraph | undefined): GraphWorkflowEditorState {
	// Lazy initializers, not an effect on `initial`: the page loads a definition by calling `reset`, so a re-render
	// with the same graph must never throw away the operator's in-progress edits.
	const [initialCanvas] = useState(() => graphToCanvas(initial));
	const [nodes, setNodes] = useState<readonly GraphWorkflowCanvasNode[]>(initialCanvas.nodes);
	const [edges, setEdges] = useState<readonly GraphWorkflowCanvasEdge[]>(initialCanvas.edges);
	const [settings, setSettings] = useState<GraphWorkflowGraphSettings>(initialCanvas.settings);
	const [baseline, setBaseline] = useState<GraphWorkflowGraph | undefined>(initial);
	const [lastNotice, setLastNotice] = useState<GraphWorkflowEditorNotice | undefined>(undefined);
	// Held in state, not derived: `graphToCanvas` has already dropped the unreadable `op` and narrowed the unknown kind
	// by the time anything downstream can look, so the wire document it was read from is the only place this is visible.
	const [loadedIssues, setLoadedIssues] = useState<readonly GraphWorkflowGraphIssue[]>(() => loadedGraphIssues(initial));
	const [mode, setModeState] = useState<GraphWorkflowEditorMode>("editing");
	const [review, setReview] = useState<GraphWorkflowEditorReview | undefined>(undefined);
	const [undoSnapshot, setUndoSnapshot] = useState<GraphWorkflowCanvasSnapshot | undefined>(undefined);
	const [viewEpoch, setViewEpoch] = useState(0);
	// The mutators read the mode through a ref: most of them are stable callbacks, and a closure over the state would
	// let a gesture through in the render between a transition and the callbacks being rebuilt.
	const modeRef = useRef<GraphWorkflowEditorMode>("editing");
	const setMode = useCallback((next: GraphWorkflowEditorMode) => {
		modeRef.current = next;
		setModeState(next);
	}, []);

	/** Whether a mutation may run. Every one that does also ends the Undo offer of the last Accept. */
	const mayMutate = useCallback((): boolean => {
		if (modeRef.current !== "editing") {
			return false;
		}
		setUndoSnapshot(undefined);
		return true;
	}, []);

	const announce = useCallback((rule: GraphWorkflowEditorNotice["rule"]) => {
		setLastNotice((previous) => ({ rule, seq: (previous?.seq ?? 0) + 1 }));
	}, []);

	const conversion = useMemo(() => canvasToGraph(nodes, edges, settings), [nodes, edges, settings]);
	// The loaded-graph issues outlive every edit: moving a node does not make an unreadable operator token readable, and
	// Save has to stay refused until the graph is loaded again or written over.
	const issues = useMemo(
		() => [...loadedIssues, ...conversion.issues, ...validateGraphWorkflowGraph(conversion.graph)],
		[conversion.graph, conversion.issues, loadedIssues],
	);

	const onNodesChange = useCallback<OnNodesChange<GraphWorkflowCanvasNode>>((changes) => {
		if (modeRef.current !== "editing") {
			return;
		}
		// React Flow reports its own measuring and selection through here too; neither is an edit, and counting them would
		// withdraw the Undo offer the moment an accepted graph renders.
		if (changes.some((change) => change.type !== "dimensions" && change.type !== "select")) {
			setUndoSnapshot(undefined);
		}
		setNodes((current) => applyNodeChanges(changes, [...current]));
	}, []);

	const onEdgesChange = useCallback<OnEdgesChange<GraphWorkflowCanvasEdge>>((changes) => {
		if (modeRef.current !== "editing") {
			return;
		}
		if (changes.some((change) => change.type !== "select")) {
			setUndoSnapshot(undefined);
		}
		setEdges((current) => applyEdgeChanges(changes, [...current]));
	}, []);

	const onConnect = useCallback<OnConnect>(
		(connection) => {
			const source = connection.source;
			const target = connection.target;
			if (source.length === 0 || target.length === 0 || !mayMutate()) {
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
				announce("parallelEdgesBothUnconditional");
				return;
			}
			const key = mintEdgeKey([...nodes.map((node) => node.id), ...edges.map((edge) => edge.id)]);
			const connected: GraphWorkflowCanvasEdge = {
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
			};
			// Wiring a Pause is the only gesture that can leave a node reading an approval where its author meant an
			// answer, so the `context` edges are computed HERE and never on a render or a validate, and only for what
			// THIS connection can have broken. An edge the operator deletes has to stay deleted.
			// A ChatInput hands on only its answer, exactly as a Pause hands on only its decision.
			const touchesPause = [source, target].some((end) => {
				const kind = nodes.find((node) => node.id === end)?.data.kind;
				return kind === "Pause" || kind === "ChatInput";
			});
			const context = touchesPause ? pauseContextEdges(nodes, [...edges, connected], { from: source, to: target }) : [];
			setEdges((current) => [...current, connected, ...context]);
			if (context.length > 0) {
				announce("pauseContextEdgeAdded");
			}
		},
		[announce, edges, mayMutate, nodes],
	);

	const addNode = useCallback(
		(kind: GraphWorkflowNodeKind, position?: XYPosition): string | undefined => {
			if (modeRef.current !== "editing") {
				return undefined;
			}
			if (nodes.length >= GRAPH_WORKFLOW_MAX_NODES) {
				announce("tooManyNodes");
				return undefined;
			}
			// The key is minted from the closure because it is this function's RETURN value; the write itself is functional,
			// like every other mutator here.
			const key = mintNodeKey(kind, [...nodes.map((node) => node.id), ...edges.map((edge) => edge.id)]);
			mayMutate();
			setNodes((current) => [
				...current,
				{
					id: key,
					type: graphWorkflowNodeTypeByKind[kind],
					position: position ?? nextFreePosition(current),
					data: defaultNodeData(kind, key),
				},
			]);
			return key;
		},
		[announce, edges, mayMutate, nodes],
	);

	const updateNodeData = useCallback(
		(key: string, patch: Partial<GraphWorkflowCanvasNodeData>) => {
			if (!mayMutate()) {
				return;
			}
			setNodes((current) =>
				current.map((node) =>
					node.id === key
						? // `kind` and `key` are re-pinned AFTER the patch: a panel that spreads a whole data object in must not
							// be able to turn an Agent card into an End one, and a rename has to go through the cascade in `renameNode`.
							{
								...node,
								data: { ...node.data, ...patch, kind: node.data.kind, key: node.data.key } as GraphWorkflowCanvasNodeData,
							}
						: node,
				),
			);
			// Taking a decision away from a Pause orphans the out-edge that routed it: React Flow stops DRAWING an edge
			// whose source handle no longer exists, but the edge stays in the graph, gets saved, and still routes at run
			// time. Pruning here is what keeps the canvas and the saved document the same graph.
			const dropped = droppedDecisions(nodes.find((node) => node.id === key)?.data, patch);
			if (dropped.length > 0) {
				setEdges((current) =>
					current.filter((edge) => edge.source !== key || !dropped.some((decision) => decision === edgeDecision(edge))),
				);
			}
		},
		[mayMutate, nodes],
	);

	const renameNode = useCallback(
		(from: string, to: string): GraphWorkflowRenameOutcome => {
			if (modeRef.current !== "editing") {
				return "invalid";
			}
			const result = renameNodeKey([...nodes], [...edges], from, to);
			if ("error" in result) {
				return result.error;
			}
			mayMutate();
			setNodes(result.nodes);
			setEdges(result.edges);
			return "ok";
		},
		[edges, mayMutate, nodes],
	);

	const updateEdgeData = useCallback(
		(edgeId: string, patch: Partial<GraphWorkflowEdgeData>) => {
			if (!mayMutate()) {
				return;
			}
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
		},
		[mayMutate],
	);

	const updateSettings = useCallback(
		(next: GraphWorkflowGraphSettings) => {
			if (mayMutate()) {
				setSettings(next);
			}
		},
		[mayMutate],
	);

	const removeNode = useCallback(
		(key: string) => {
			if (!mayMutate()) {
				return;
			}
			setNodes((current) => current.filter((node) => node.id !== key));
			// The incident edges go with it: leaving them behind would produce `unknownEdgeEndpoint` on every later validate.
			// Destructured rather than `edge.target`: `CheckEventCurrentTargetInUpdaters` flags any `.target` read inside a
			// functional updater, and React Flow's edge endpoint happens to share the name of the DOM event property it guards.
			setEdges((current) => current.filter(({ source, target }) => source !== key && target !== key));
		},
		[mayMutate],
	);

	const removeEdge = useCallback(
		(edgeId: string) => {
			if (mayMutate()) {
				setEdges((current) => current.filter((edge) => edge.id !== edgeId));
			}
		},
		[mayMutate],
	);

	const autoArrange = useCallback(() => {
		if (!mayMutate()) {
			return;
		}
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
	}, [edges, mayMutate]);

	const reset = useCallback(
		(graph: GraphWorkflowGraph) => {
			const canvas = graphToCanvas(graph);
			setNodes(canvas.nodes);
			setEdges(canvas.edges);
			setSettings(canvas.settings);
			setBaseline(graph);
			setLoadedIssues(loadedGraphIssues(graph));
			setLastNotice(undefined);
			// A load is a different graph: whatever was proposed or accepted against the old one no longer applies.
			setReview(undefined);
			setUndoSnapshot(undefined);
			setMode("editing");
		},
		[setMode],
	);

	const markSaved = useCallback((graph: GraphWorkflowGraph) => {
		setBaseline(graph);
		setUndoSnapshot(undefined);
		// The save wrote what the canvas holds, so whatever the server had stored that the canvas could not represent went
		// with it.
		setLoadedIssues([]);
	}, []);

	const dismissNotice = useCallback(() => {
		setLastNotice(undefined);
	}, []);

	const beginGenerating = useCallback((): boolean => {
		if (modeRef.current !== "editing") {
			return false;
		}
		setMode("generating");
		return true;
	}, [setMode]);

	const endGenerating = useCallback(() => {
		if (modeRef.current === "generating") {
			setMode("editing");
		}
	}, [setMode]);

	const showProposal = useCallback(
		(input: GraphWorkflowProposalInput) => {
			if (modeRef.current !== "generating") {
				return;
			}
			setReview({ ...input, canvas: designerProposalCanvas({ nodes, edges }, input.proposal, input.changes) });
			setMode("reviewing");
			setViewEpoch((epoch) => epoch + 1);
		},
		[edges, nodes, setMode],
	);

	const rejectProposal = useCallback(() => {
		if (modeRef.current === "reviewing") {
			setReview(undefined);
			setMode("editing");
		}
	}, [setMode]);

	const isReviewStale = review !== undefined && !graphWorkflowsEqual(review.basis, conversion.graph);

	const acceptProposal = useCallback((): boolean => {
		if (modeRef.current !== "reviewing" || review === undefined || !review.valid || isReviewStale) {
			return false;
		}
		setUndoSnapshot({ nodes, edges, settings, loadedIssues });
		const canvas = graphToCanvas(review.proposal);
		setNodes(canvas.nodes);
		setEdges(canvas.edges);
		setSettings(canvas.settings);
		setLoadedIssues(loadedGraphIssues(review.proposal));
		setReview(undefined);
		setMode("editing");
		setViewEpoch((epoch) => epoch + 1);
		return true;
	}, [edges, isReviewStale, loadedIssues, nodes, review, setMode, settings]);

	const undoAccept = useCallback(() => {
		if (modeRef.current !== "editing" || undoSnapshot === undefined) {
			return;
		}
		setNodes(undoSnapshot.nodes);
		setEdges(undoSnapshot.edges);
		setSettings(undoSnapshot.settings);
		setLoadedIssues(undoSnapshot.loadedIssues);
		setUndoSnapshot(undefined);
		setViewEpoch((epoch) => epoch + 1);
	}, [undoSnapshot]);

	return {
		nodes,
		edges,
		settings,
		graph: conversion.graph,
		issues,
		isDirty: !graphWorkflowsEqual(conversion.graph, baseline),
		canAddNode: nodes.length < GRAPH_WORKFLOW_MAX_NODES,
		lastNotice,
		onNodesChange,
		onEdgesChange,
		onConnect,
		addNode,
		updateNodeData,
		renameNode,
		updateEdgeData,
		updateSettings,
		removeNode,
		removeEdge,
		autoArrange,
		reset,
		markSaved,
		dismissNotice,
		mode,
		review,
		isReviewStale,
		canUndoAccept: undoSnapshot !== undefined,
		viewEpoch,
		beginGenerating,
		endGenerating,
		showProposal,
		rejectProposal,
		acceptProposal,
		undoAccept,
	};
}
