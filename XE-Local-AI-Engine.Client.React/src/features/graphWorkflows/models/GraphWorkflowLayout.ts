// A layered left-to-right DAG layout. Pure: no React, no React Flow, no dependency — dagre and elkjs are both correct
// and both a new production dependency for ~70 lines of arithmetic.
//
// This is a COPY of `devWorkflows/models/DevWorkflowLayout.ts` (features never import each other — see
// `no-cross-feature`), trimmed of the materialization tie-break: a Graph Workflow never clones a template at run time,
// so a node's identity is its key and the tie-break is that key alone. The ranking, the barycenter pass, the
// top-alignment and the cycle guard are the original's, unchanged.
//
// rank(n)     = 0 with no inbound edge, else 1 + max(rank(pred))   — longest path, taken in Kahn order
// orderInRank = ONE barycenter pass over predecessor orders, ties broken by key
// x = rank * 280, y = indexInRank * 130                            — TOP-ALIGNED
//
// Used for three things: a node that arrives without a `position` (an older graph, or S4's Preview importer, which
// emits none), the editor's "Auto-arrange", and the run view's nodes-only fallback when the run's graph hash no longer
// matches the definition's.
//
// ponytail: single barycenter pass, no crossing-minimisation iterations — swap in dagre if edge crossings become
// unreadable on real fan-out graphs; this module's signature is the seam.

export const RANK_SPACING_X = 280;
export const NODE_SPACING_Y = 130;

export interface GraphWorkflowLayoutNode {
	/** The node key. It is also the React Flow node id, so one identifier addresses the card, the edge and the node run. */
	readonly key: string;
}

export interface GraphWorkflowLayoutEdge {
	readonly from: string;
	readonly to: string;
}

export interface GraphWorkflowLayoutPosition {
	readonly x: number;
	readonly y: number;
	readonly rank: number;
	readonly indexInRank: number;
}

export interface GraphWorkflowLayout {
	readonly positions: ReadonlyMap<string, GraphWorkflowLayoutPosition>;
	/** `${from}>${to}` for every edge that does not move forward a rank. Empty for an acyclic graph, which v1 requires. */
	readonly backEdgeKeys: ReadonlySet<string>;
	readonly rankCount: number;
}

function edgeKey(edge: GraphWorkflowLayoutEdge): string {
	return `${edge.from}>${edge.to}`;
}

/**
 * Longest-path ranks in Kahn order. A graph that cannot drain is cyclic: whatever is left is parked one rank past
 * everything that did drain, so the layout terminates and every node is placed exactly once. The validator refuses a
 * cyclic graph, but it is advisory and the server is the authority — a UI that hangs is a worse failure than a UI that
 * draws a strange graph.
 */
function rankNodes(nodes: readonly GraphWorkflowLayoutNode[], edges: readonly GraphWorkflowLayoutEdge[]): Map<string, number> {
	const ranks = new Map<string, number>(nodes.map((node) => [node.key, 0]));
	const successors = new Map<string, string[]>();
	const inDegree = new Map<string, number>(nodes.map((node) => [node.key, 0]));
	for (const edge of edges) {
		successors.set(edge.from, [...(successors.get(edge.from) ?? []), edge.to]);
		inDegree.set(edge.to, (inDegree.get(edge.to) ?? 0) + 1);
	}

	const queue = nodes.filter((node) => (inDegree.get(node.key) ?? 0) === 0).map((node) => node.key);
	const drained = new Set<string>();
	for (let cursor = 0; cursor < queue.length; cursor += 1) {
		const key = queue[cursor] ?? "";
		drained.add(key);
		for (const next of successors.get(key) ?? []) {
			ranks.set(next, Math.max(ranks.get(next) ?? 0, (ranks.get(key) ?? 0) + 1));
			const remaining = (inDegree.get(next) ?? 0) - 1;
			inDegree.set(next, remaining);
			if (remaining === 0) {
				queue.push(next);
			}
		}
	}

	if (drained.size === nodes.length) {
		return ranks;
	}

	const maxDrainedRank = nodes.reduce(
		(highest, node) => (drained.has(node.key) ? Math.max(highest, ranks.get(node.key) ?? 0) : highest),
		-1,
	);
	for (const node of nodes) {
		if (!drained.has(node.key)) {
			ranks.set(node.key, maxDrainedRank + 1);
		}
	}
	return ranks;
}

export function layoutGraphWorkflow(
	nodes: readonly GraphWorkflowLayoutNode[],
	edges: readonly GraphWorkflowLayoutEdge[],
): GraphWorkflowLayout {
	const ranks = rankNodes(nodes, edges);
	const predecessors = new Map<string, string[]>();
	for (const edge of edges) {
		predecessors.set(edge.to, [...(predecessors.get(edge.to) ?? []), edge.from]);
	}

	const rankCount = nodes.reduce((highest, node) => Math.max(highest, ranks.get(node.key) ?? 0), -1) + 1;
	const positions = new Map<string, GraphWorkflowLayoutPosition>();
	const orderByKey = new Map<string, number>();

	for (let rank = 0; rank < rankCount; rank += 1) {
		const inRank = nodes.filter((node) => ranks.get(node.key) === rank);
		// One barycenter pass over the orders already fixed in the ranks to the left. A node whose predecessors are all
		// in this rank or later (only reachable through the cycle guard) has no barycenter and falls to the end, where
		// the key tie-break still orders it deterministically.
		const barycenters = new Map<string, number>(
			inRank.map((node) => {
				const orders = (predecessors.get(node.key) ?? []).flatMap((from) => {
					const order = orderByKey.get(from);
					return order === undefined ? [] : [order];
				});
				return [
					node.key,
					orders.length === 0 ? Number.POSITIVE_INFINITY : orders.reduce((sum, order) => sum + order, 0) / orders.length,
				];
			}),
		);
		const ordered = inRank.toSorted((left, right) => {
			const leftBarycenter = barycenters.get(left.key) ?? Number.POSITIVE_INFINITY;
			const rightBarycenter = barycenters.get(right.key) ?? Number.POSITIVE_INFINITY;
			if (leftBarycenter !== rightBarycenter) {
				return leftBarycenter < rightBarycenter ? -1 : 1;
			}
			// Total by construction: every comparison ends at the key, which is unique per graph, so two layouts of the
			// same graph cannot disagree.
			return left.key.localeCompare(right.key);
		});

		for (const [indexInRank, node] of ordered.entries()) {
			orderByKey.set(node.key, indexInRank);
			positions.set(node.key, {
				x: rank * RANK_SPACING_X,
				// TOP-ALIGNED, not centred. A y that divides by the rank's population moves every node in that rank the
				// moment another one joins it, so an operator's half-arranged graph would shuffle under them as they add
				// nodes. Here a node's y depends only on its own index in its rank.
				y: indexInRank * NODE_SPACING_Y,
				rank,
				indexInRank,
			});
		}
	}

	// In a DAG every edge moves forward at least one rank, so anything that does not is the cycle the guard above
	// parked. Tagging is over the whole cycle rather than one chosen chord — there is no honest way to pick which edge
	// of a cycle is "the" back edge.
	const backEdgeKeys = new Set<string>(
		edges.filter((edge) => (ranks.get(edge.to) ?? 0) <= (ranks.get(edge.from) ?? 0)).map((edge) => edgeKey(edge)),
	);

	return { positions, backEdgeKeys, rankCount };
}
