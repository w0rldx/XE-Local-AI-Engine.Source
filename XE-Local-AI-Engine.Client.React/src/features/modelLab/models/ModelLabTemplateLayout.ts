// A layered top-to-bottom DAG layout for a block template. Pure: no React, no React Flow, no dependency.
//
// A COPY of `graphWorkflows/models/GraphWorkflowLayout.ts` (features never import each other, see `no-cross-feature`),
// trimmed to positions only and turned on its side, because a block reads from its input down to its output. The
// ranking, the single barycenter pass and the cycle guard are the original's.
//
// rank(n)     = 0 with no inbound edge, else 1 + max(rank(pred)), longest path in Kahn order
// orderInRank = ONE barycenter pass over predecessor orders, ties broken by id
// x = indexInRank * 220, y = rank * 90

const NODE_SPACING_X = 220;
const RANK_SPACING_Y = 90;

interface LayoutEdge {
	readonly from: string;
	readonly to: string;
}

// A graph that cannot drain is cyclic: what is left is parked one rank past everything that drained, so every node is
// placed once and the layout terminates. Templates are static server data and acyclic, but a hang is the worse failure.
function rankNodes(ids: readonly string[], edges: readonly LayoutEdge[]): Map<string, number> {
	const ranks = new Map<string, number>(ids.map((id) => [id, 0]));
	const successors = new Map<string, string[]>();
	const inDegree = new Map<string, number>(ids.map((id) => [id, 0]));
	for (const edge of edges) {
		successors.set(edge.from, [...(successors.get(edge.from) ?? []), edge.to]);
		inDegree.set(edge.to, (inDegree.get(edge.to) ?? 0) + 1);
	}

	const queue = ids.filter((id) => (inDegree.get(id) ?? 0) === 0);
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

	if (drained.size < ids.length) {
		const maxDrainedRank = ids.reduce((highest, id) => (drained.has(id) ? Math.max(highest, ranks.get(id) ?? 0) : highest), -1);
		for (const id of ids) {
			if (!drained.has(id)) {
				ranks.set(id, maxDrainedRank + 1);
			}
		}
	}
	return ranks;
}

export function layoutTemplate(ids: readonly string[], edges: readonly LayoutEdge[]): Map<string, { x: number; y: number }> {
	const ranks = rankNodes(ids, edges);
	const predecessors = new Map<string, string[]>();
	for (const edge of edges) {
		predecessors.set(edge.to, [...(predecessors.get(edge.to) ?? []), edge.from]);
	}

	const rankCount = ids.reduce((highest, id) => Math.max(highest, ranks.get(id) ?? 0), -1) + 1;
	const positions = new Map<string, { x: number; y: number }>();
	const orderById = new Map<string, number>();
	for (let rank = 0; rank < rankCount; rank += 1) {
		const inRank = ids.filter((id) => ranks.get(id) === rank);
		const barycenters = new Map<string, number>(
			inRank.map((id) => {
				const orders = (predecessors.get(id) ?? []).flatMap((from) => {
					const order = orderById.get(from);
					return order === undefined ? [] : [order];
				});
				return [
					id,
					orders.length === 0 ? Number.POSITIVE_INFINITY : orders.reduce((sum, order) => sum + order, 0) / orders.length,
				];
			}),
		);
		const ordered = inRank.toSorted((left, right) => {
			const leftBarycenter = barycenters.get(left) ?? Number.POSITIVE_INFINITY;
			const rightBarycenter = barycenters.get(right) ?? Number.POSITIVE_INFINITY;
			if (leftBarycenter !== rightBarycenter) {
				return leftBarycenter < rightBarycenter ? -1 : 1;
			}
			return left.localeCompare(right);
		});
		for (const [indexInRank, id] of ordered.entries()) {
			orderById.set(id, indexInRank);
			positions.set(id, { x: indexInRank * NODE_SPACING_X, y: rank * RANK_SPACING_Y });
		}
	}
	return positions;
}
