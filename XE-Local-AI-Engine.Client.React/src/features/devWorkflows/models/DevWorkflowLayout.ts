// A layered left-to-right DAG layout, hand-rolled (P4 §2.3.2). Pure: no React, no React Flow, no dependency — dagre
// and elkjs are both correct and both a new production dependency for ~70 lines of arithmetic.
//
// rank(n)     = 0 with no inbound edge, else 1 + max(rank(pred))   — longest path, taken in Kahn order
// orderInRank = definition nodes before materialized clones, then ONE barycenter pass over predecessor orders,
//               ties broken by (materializationGroupKey, materializationIndex, nodeKey, id)
// x = rank * 280, y = indexInRank * 130                            — TOP-ALIGNED, see below
//
// ponytail: single barycenter pass, no crossing-minimisation iterations — swap in dagre if edge crossings become
// unreadable on real fan-out graphs; this module's signature is the seam.

const RANK_SPACING_X = 280;
const NODE_SPACING_Y = 130;

export interface DevWorkflowLayoutNode {
	/** React Flow node id — the server node-run id for a real node, a synthesised id for an anchor. */
	readonly id: string;
	/** The tie-break key of last resort before `id`, so sibling order follows the definition rather than a GUID. */
	readonly nodeKey: string;
	/**
	 * Which materialization this clone belongs to. The server's `materializationGroupId` where there is one, falling
	 * back to the template node key: two decompositions of the SAME template are distinct groups, and the key alone
	 * cannot tell them apart.
	 */
	readonly materializationGroupKey?: string;
	readonly materializationIndex?: number;
}

export interface DevWorkflowLayoutEdge {
	readonly from: string;
	readonly to: string;
}

export interface DevWorkflowLayoutPosition {
	readonly x: number;
	readonly y: number;
	readonly rank: number;
	readonly indexInRank: number;
}

export interface DevWorkflowLayout {
	readonly positions: ReadonlyMap<string, DevWorkflowLayoutPosition>;
	/** `${from}>${to}` for every edge that does not move forward a rank. Empty for the acyclic graphs X9 guarantees. */
	readonly backEdgeKeys: ReadonlySet<string>;
	readonly rankCount: number;
}

export function devWorkflowEdgeKey(edge: DevWorkflowLayoutEdge): string {
	return `${edge.from}>${edge.to}`;
}

/** Total by construction — every comparison ends at `id`, so two layouts of the same graph cannot disagree. */
function compareTieBreak(left: DevWorkflowLayoutNode, right: DevWorkflowLayoutNode): number {
	// Origin before index, so two decompositions sharing a rank read as two blocks rather than an interleave. Which
	// origin sorts first does not matter; that they never split does.
	const origin = (left.materializationGroupKey ?? "").localeCompare(right.materializationGroupKey ?? "");
	if (origin !== 0) {
		return origin;
	}
	const index = (left.materializationIndex ?? -1) - (right.materializationIndex ?? -1);
	if (index !== 0) {
		return index;
	}
	const key = left.nodeKey.localeCompare(right.nodeKey);
	return key !== 0 ? key : left.id.localeCompare(right.id);
}

/**
 * Longest-path ranks in Kahn order. A graph that cannot drain is cyclic: whatever is left is parked one rank past
 * everything that did drain, in id order, so the layout terminates and every node is placed exactly once. X9 makes v1
 * definitions acyclic; this guard is four lines of defence in depth, because a UI that hangs is a worse failure than a
 * UI that draws a strange graph.
 */
function rankNodes(
	nodes: readonly DevWorkflowLayoutNode[],
	edges: readonly DevWorkflowLayoutEdge[],
): Map<string, number> {
	const ranks = new Map<string, number>(nodes.map((node) => [node.id, 0]));
	const successors = new Map<string, string[]>();
	const inDegree = new Map<string, number>(nodes.map((node) => [node.id, 0]));
	for (const edge of edges) {
		successors.set(edge.from, [...(successors.get(edge.from) ?? []), edge.to]);
		inDegree.set(edge.to, (inDegree.get(edge.to) ?? 0) + 1);
	}

	const queue = nodes.filter((node) => (inDegree.get(node.id) ?? 0) === 0).map((node) => node.id);
	const drained = new Set<string>();
	for (let cursor = 0; cursor < queue.length; cursor += 1) {
		const id = queue[cursor] ?? "";
		drained.add(id);
		for (const next of successors.get(id) ?? []) {
			ranks.set(next, Math.max(ranks.get(next) ?? 0, (ranks.get(id) ?? 0) + 1));
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
		(highest, node) => (drained.has(node.id) ? Math.max(highest, ranks.get(node.id) ?? 0) : highest),
		-1,
	);
	for (const node of nodes) {
		if (!drained.has(node.id)) {
			ranks.set(node.id, maxDrainedRank + 1);
		}
	}
	return ranks;
}

export function layoutDevWorkflowGraph(
	nodes: readonly DevWorkflowLayoutNode[],
	edges: readonly DevWorkflowLayoutEdge[],
): DevWorkflowLayout {
	const ranks = rankNodes(nodes, edges);
	const predecessors = new Map<string, string[]>();
	for (const edge of edges) {
		predecessors.set(edge.to, [...(predecessors.get(edge.to) ?? []), edge.from]);
	}

	const rankCount = nodes.reduce((highest, node) => Math.max(highest, ranks.get(node.id) ?? 0), -1) + 1;
	const positions = new Map<string, DevWorkflowLayoutPosition>();
	const orderById = new Map<string, number>();

	for (let rank = 0; rank < rankCount; rank += 1) {
		const inRank = nodes.filter((node) => ranks.get(node.id) === rank);
		// One barycenter pass over the orders already fixed in the ranks to the left. A node whose predecessors are all
		// in this rank or later (only reachable through the cycle guard) has no barycenter and falls to the end, where
		// the tie-break still orders it deterministically.
		const barycenters = new Map<string, number>(
			inRank.map((node) => {
				const orders = (predecessors.get(node.id) ?? []).flatMap((from) => {
					const order = orderById.get(from);
					return order === undefined ? [] : [order];
				});
				return [
					node.id,
					orders.length === 0
						? Number.POSITIVE_INFINITY
						: orders.reduce((sum, order) => sum + order, 0) / orders.length,
				];
			}),
		);
		const ordered = inRank.toSorted((left, right) => {
			// Materialized clones sort behind the definition's own nodes, AHEAD of the barycenter rather than only as a
			// tie-break. Slice C grows a rank that already holds a node, and letting a clone's barycenter push a
			// pre-existing node down the rank would move it — the exact jump top-alignment exists to prevent. Within the
			// clones the barycenter still orders the groups.
			const materialized =
				Number(left.materializationGroupKey !== undefined) - Number(right.materializationGroupKey !== undefined);
			if (materialized !== 0) {
				return materialized;
			}
			const leftBarycenter = barycenters.get(left.id) ?? Number.POSITIVE_INFINITY;
			const rightBarycenter = barycenters.get(right.id) ?? Number.POSITIVE_INFINITY;
			if (leftBarycenter !== rightBarycenter) {
				return leftBarycenter < rightBarycenter ? -1 : 1;
			}
			return compareTieBreak(left, right);
		});

		ordered.forEach((node, indexInRank) => {
			orderById.set(node.id, indexInRank);
			positions.set(node.id, {
				x: rank * RANK_SPACING_X,
				// TOP-ALIGNED, not centred (R-C5). A y that divides by the rank's population moves every node in that rank
				// the moment a decomposition lands in it, and Slice C's canonical shape is exactly that: clones arriving
				// beside a node that has held a row since run start. Here a node's y depends only on its own index, and the
				// sort above keeps the clones behind the nodes that were already there — so their indices, and their
				// positions, do not change. The graph hangs from the top instead of straddling a centre line; that is the
				// price, and it is paid once at render rather than every time the runtime expands the graph.
				y: indexInRank * NODE_SPACING_Y,
				rank,
				indexInRank,
			});
		});
	}

	// In a DAG every edge moves forward at least one rank, so anything that does not is the cycle the guard above
	// parked. Tagging is over the whole cycle rather than one chosen chord — there is no honest way to pick which edge
	// of a cycle is "the" back edge.
	const backEdgeKeys = new Set<string>(
		edges
			.filter((edge) => (ranks.get(edge.to) ?? 0) <= (ranks.get(edge.from) ?? 0))
			.map((edge) => devWorkflowEdgeKey(edge)),
	);

	return { positions, backEdgeKeys, rankCount };
}
