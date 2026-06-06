import type { PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";

// Client-side mirror of PreviewWorkflowGraphValidator.cs (plan §7.3). A workflow is a STRICTLY LINEAR chain:
// exactly one Start, exactly one reachable End, in-degree ≤ 1 and out-degree ≤ 1 per node, every Agent node
// carries a model + instructions, and at least one Agent node lies between Start and End. The backend remains
// the authority (it re-validates and returns 400); this is purely the Execute-disabled-when-invalid UX gate so
// the operator gets immediate feedback without a round-trip. Returns the i18n KEYS of the failures (not text)
// so the page renders them through `t`, mirroring how the backend returns structured reasons.

export interface PreviewWorkflowValidationResult {
	readonly isValid: boolean;
	readonly errorKeys: readonly string[];
}

const KEY_PREFIX = "pages.preview.validation.";

export function validatePreviewGraph(graph: PreviewWorkflowGraph): PreviewWorkflowValidationResult {
	const errorKeys = new Set<string>();
	const nodes = graph.nodes;
	const edges = graph.edges;

	if (nodes.length === 0) {
		return { isValid: false, errorKeys: [`${KEY_PREFIX}empty`] };
	}

	// Node ids must be unique + non-empty (every downstream rule keys on the id).
	const nodesById = new Map<string, (typeof nodes)[number]>();
	for (const node of nodes) {
		if (node.id.trim().length === 0) {
			errorKeys.add(`${KEY_PREFIX}emptyId`);
			continue;
		}
		if (nodesById.has(node.id)) {
			errorKeys.add(`${KEY_PREFIX}duplicateId`);
		} else {
			nodesById.set(node.id, node);
		}
	}

	const startNodes = nodes.filter((node) => node.kind === "Start");
	const endNodes = nodes.filter((node) => node.kind === "End");
	if (startNodes.length !== 1) {
		errorKeys.add(`${KEY_PREFIX}startCount`);
	}
	if (endNodes.length !== 1) {
		errorKeys.add(`${KEY_PREFIX}endCount`);
	}

	// Edges must reference known nodes before reasoning about degrees / reachability.
	for (const edge of edges) {
		if (!nodesById.has(edge.sourceId) || !nodesById.has(edge.targetId)) {
			errorKeys.add(`${KEY_PREFIX}unknownEdge`);
		}
	}

	// Linearity: in-degree ≤ 1 AND out-degree ≤ 1 per node (forces a single chain, rejects fan-out/fan-in).
	const inDegree = new Map<string, number>();
	const outDegree = new Map<string, number>();
	const adjacency = new Map<string, string[]>();
	for (const id of nodesById.keys()) {
		inDegree.set(id, 0);
		outDegree.set(id, 0);
		adjacency.set(id, []);
	}
	for (const edge of edges) {
		if (outDegree.has(edge.sourceId)) {
			outDegree.set(edge.sourceId, (outDegree.get(edge.sourceId) ?? 0) + 1);
			adjacency.get(edge.sourceId)?.push(edge.targetId);
		}
		if (inDegree.has(edge.targetId)) {
			inDegree.set(edge.targetId, (inDegree.get(edge.targetId) ?? 0) + 1);
		}
	}
	for (const count of outDegree.values()) {
		if (count > 1) {
			errorKeys.add(`${KEY_PREFIX}notLinear`);
		}
	}
	for (const count of inDegree.values()) {
		if (count > 1) {
			errorKeys.add(`${KEY_PREFIX}notLinear`);
		}
	}

	// Every Agent node must carry a model and instructions.
	for (const agent of nodes.filter((node) => node.kind === "Agent")) {
		if ((agent.model ?? "").trim().length === 0) {
			errorKeys.add(`${KEY_PREFIX}agentModel`);
		}
		if ((agent.instructions ?? "").trim().length === 0) {
			errorKeys.add(`${KEY_PREFIX}agentInstructions`);
		}
	}

	// Reachability + "≥ 1 Agent between Start and End" only make sense once the structural rules hold.
	const startNode = startNodes[0];
	const endNode = endNodes[0];
	if (errorKeys.size === 0 && startNode !== undefined && endNode !== undefined) {
		validateReachableAgentPath(startNode.id, endNode.id, nodesById, adjacency, errorKeys);
	}

	return { isValid: errorKeys.size === 0, errorKeys: [...errorKeys] };
}

function validateReachableAgentPath(
	startId: string,
	endId: string,
	nodesById: Map<string, { kind: string }>,
	adjacency: Map<string, string[]>,
	errorKeys: Set<string>,
): void {
	const visited = new Set<string>();
	let current: string | null = startId;
	let reachedEnd = false;
	let agentCount = 0;

	while (current !== null && !visited.has(current)) {
		visited.add(current);
		const node = nodesById.get(current);
		if (node === undefined) {
			break;
		}
		if (node.kind === "Agent") {
			agentCount++;
		}
		if (current === endId) {
			reachedEnd = true;
			break;
		}
		const successors: string[] = adjacency.get(current) ?? [];
		const nextNode: string | undefined = successors[0];
		current = successors.length === 1 && nextNode !== undefined ? nextNode : null;
	}

	if (!reachedEnd) {
		errorKeys.add(`${KEY_PREFIX}endUnreachable`);
		return;
	}
	if (agentCount === 0) {
		errorKeys.add(`${KEY_PREFIX}noAgent`);
	}
}
