// Authoring helpers for the graph editor: a fresh node's defaults, key minting, the rename cascade and what a gesture
// on the canvas prefills or places. Pure: no React.

import type { XYPosition } from "@xyflow/react";

import {
	type GraphWorkflowCanvas,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasEdgeCondition,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	asGraphWorkflowDecisionKind,
	GRAPH_WORKFLOW_KEY_PATTERN,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowNodeKind,
	graphWorkflowDefaultMaxAttempts,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/** A fresh node's data, with its defaults — including `maxAttempts` (3 for Agent and Tool, else 1). */
export function defaultNodeData(kind: GraphWorkflowNodeKind, key: string): GraphWorkflowCanvasNodeData {
	const base = { key, label: "", joinPolicy: "All" as const, maxAttempts: graphWorkflowDefaultMaxAttempts(kind) };
	switch (kind) {
		case "Start":
			return { ...base, kind, inputSchema: null, defaultInput: null, aliases: [] };
		case "Agent":
			return {
				...base,
				kind,
				agentDefinitionId: null,
				instructions: "",
				model: null,
				reasoningEffort: null,
				responseJsonSchema: null,
				includeUpstreamOutputs: true,
			};
		case "LlmCall":
			return {
				...base,
				kind,
				model: null,
				systemPrompt: null,
				prompt: "",
				inputBindings: [],
				reasoningEffort: null,
				responseJsonSchema: null,
				samplingOptions: {},
			};
		case "Tool":
			return { ...base, kind, toolName: null, argumentsJson: "", argumentBindings: [], allowedUrls: [] };
		case "Condition":
			return { ...base, kind, path: null };
		case "Parallel":
		case "Join":
			return { ...base, kind };
		case "Pause":
			return { ...base, kind, prompt: "", allowedDecisions: ["Approve", "Reject"], requireComment: false };
		case "ChatInput":
			return { ...base, kind, prompt: "" };
		case "DecisionModel":
			return {
				...base,
				kind,
				question: "",
				questionType: null,
				labels: [],
				criteria: {},
				provider: null,
				model: null,
				inputBindings: [],
			};
		case "DevTask":
			return {
				...base,
				kind,
				action: "implement",
				title: "",
				requirements: "",
				acceptanceCriteria: [],
				instructions: "",
				inputBindings: [],
				task: null,
			};
		// `default` IS the End case — see `nodeDataFromWire` in `GraphWorkflowCanvasConversion`.
		default:
			return { ...base, kind, outcome: "completed", resultPath: null };
	}
}

function mintKey(prefix: string, existingKeys: Iterable<string>): string {
	const taken = new Set(existingKeys);
	for (let index = 1; ; index += 1) {
		const candidate = `${prefix}${index}`;
		if (!taken.has(candidate)) {
			return candidate;
		}
	}
}

/** `agent-1`, `agent-2`, … — the kind slug plus the lowest free integer, over the ONE key namespace. */
export function mintNodeKey(kind: GraphWorkflowNodeKind, existingKeys: Iterable<string>): string {
	return mintKey(`${graphWorkflowNodeTypeByKind[kind]}-`, existingKeys);
}

/** `e1`, `e2`, … — Preview's `${source}->${target}` scheme cannot be used: a Pause routing both decisions to one End
 * is the natural authoring shape and that scheme collides on it. */
export function mintEdgeKey(existingKeys: Iterable<string>): string {
	return mintKey("e", existingKeys);
}

export type GraphWorkflowRenameResult = GraphWorkflowCanvas | { readonly error: "collision" | "invalid" };

/**
 * Renaming a key is a GRAPH operation, not a field edit. Without the cascade a rename silently produces
 * `unknownEdgeEndpoint` on the next validate. Pure; the editor swaps in the result.
 *
 * Safe to offer at all because the editor only ever edits a definition and a run pins its own graph copy, so no run is
 * disturbed. It refuses a name that collides with any existing node OR edge key (one namespace) and one that does not
 * match the server's charset.
 */
export function renameNodeKey(
	nodes: readonly GraphWorkflowCanvasNode[],
	edges: readonly GraphWorkflowCanvasEdge[],
	from: string,
	to: string,
): GraphWorkflowRenameResult {
	if (!GRAPH_WORKFLOW_KEY_PATTERN.test(to)) {
		return { error: "invalid" };
	}
	if (to !== from && (nodes.some((node) => node.id === to) || edges.some((edge) => edge.id === to))) {
		return { error: "collision" };
	}
	const renamedNodes = nodes.map((node) => {
		const renamed = node.id === from ? { ...node, id: to, data: { ...node.data, key: to } } : node;
		// An `apply` node names its `implement` node by key, so that reference follows the rename like an edge endpoint.
		return renamed.data.kind === "DevTask" && renamed.data.task === from
			? { ...renamed, data: { ...renamed.data, task: to } }
			: renamed;
	});
	// Endpoints only. An edge condition is never rewritten: a Pause out-edge's `condition.value` is a DECISION name, so
	// following a rename into it could only corrupt the routing of a node someone happened to key `Approve`.
	const renamedEdges = edges.map((edge) => ({
		...edge,
		source: edge.source === from ? to : edge.source,
		target: edge.target === from ? to : edge.target,
	}));
	return { nodes: renamedNodes, edges: renamedEdges };
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
export function connectionPrefill(
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
export function droppedDecisions(
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
export function edgeDecision(edge: GraphWorkflowCanvasEdge): string {
	return edge.sourceHandle ?? edge.data?.condition?.value ?? "";
}

/**
 * Where a palette click drops a node when nothing said where: the first staggered slot no node already occupies.
 * Staggering by the node COUNT would reuse a slot the moment anything was deleted, dropping the new card on top of one.
 */
export function nextFreePosition(nodes: readonly GraphWorkflowCanvasNode[]): XYPosition {
	for (let index = 0; ; index += 1) {
		const candidate = { x: 320, y: 80 + index * 60 };
		if (!nodes.some((node) => node.position.x === candidate.x && node.position.y === candidate.y)) {
			return candidate;
		}
	}
}
