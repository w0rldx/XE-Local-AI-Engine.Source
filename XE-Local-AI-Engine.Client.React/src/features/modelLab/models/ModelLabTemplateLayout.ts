// A layered top-to-bottom DAG layout for a block template or a block's PyTorch graph. Pure: no React, no React Flow, no
// dependency.
//
// A COPY of `graphWorkflows/models/GraphWorkflowLayout.ts` (features never import each other, see `no-cross-feature`),
// trimmed to positions only and turned on its side, because a block reads from its input down to its output. The
// ranking and the single barycenter pass are the original's.
//
// rank(n)     = 0 with no inbound edge, else 1 + max(rank(pred)), longest path in Kahn order
// orderInRank = ONE barycenter pass over predecessor orders, ties broken by id
// x = (orderInRank mod maxNodesPerRow) * 220, y = row * 90: a rank wider than maxNodesPerRow wraps onto rows of its own

const NODE_SPACING_X = 220;
export const RANK_SPACING_Y = 90;

// Four 200 px nodes fit a desktop canvas at full zoom; a wider rank would open as a strip too wide to read.
export const maxNodesPerRow = 4;

interface LayoutEdge {
	readonly from: string;
	readonly to: string;
}

// A PyTorch graph groups ops by source line, so a line that runs twice makes a cycle. When the queue drains early, the
// first node left in input order is released as if its remaining inputs had drained: a cycle costs its back edges, not
// a rank holding everything that was left. Every node is placed once and the layout terminates.
function rankNodes(ids: readonly string[], edges: readonly LayoutEdge[]): Map<string, number> {
	const ranks = new Map<string, number>(ids.map((id) => [id, 0]));
	const successors = new Map<string, string[]>();
	const inDegree = new Map<string, number>(ids.map((id) => [id, 0]));
	for (const edge of edges) {
		successors.set(edge.from, [...(successors.get(edge.from) ?? []), edge.to]);
		inDegree.set(edge.to, (inDegree.get(edge.to) ?? 0) + 1);
	}

	const queue = ids.filter((id) => (inDegree.get(id) ?? 0) === 0);
	const queued = new Set(queue);
	for (let cursor = 0; queued.size < ranks.size || cursor < queue.length; cursor += 1) {
		if (cursor === queue.length) {
			const released = ids.find((id) => !queued.has(id)) ?? "";
			queue.push(released);
			queued.add(released);
		}
		const id = queue[cursor] ?? "";
		for (const next of successors.get(id) ?? []) {
			// A back edge into a node already placed.
			if (queued.has(next)) {
				continue;
			}
			ranks.set(next, Math.max(ranks.get(next) ?? 0, (ranks.get(id) ?? 0) + 1));
			const remaining = (inDegree.get(next) ?? 0) - 1;
			inDegree.set(next, remaining);
			if (remaining === 0) {
				queue.push(next);
				queued.add(next);
			}
		}
	}
	return ranks;
}

export function layoutTemplate(ids: readonly string[], edges: readonly LayoutEdge[]): Map<string, { x: number; y: number }> {
	const known = new Set(ids);
	const knownEdges = edges.filter((edge) => known.has(edge.from) && known.has(edge.to));
	const ranks = rankNodes([...known], knownEdges);
	const predecessors = new Map<string, string[]>();
	for (const edge of knownEdges) {
		predecessors.set(edge.to, [...(predecessors.get(edge.to) ?? []), edge.from]);
	}

	const rankCount = Math.max(-1, ...ranks.values()) + 1;
	const positions = new Map<string, { x: number; y: number }>();
	const orderById = new Map<string, number>();
	let row = 0;
	for (let rank = 0; rank < rankCount; rank += 1) {
		const inRank = [...known].filter((id) => ranks.get(id) === rank);
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
			positions.set(id, {
				x: (indexInRank % maxNodesPerRow) * NODE_SPACING_X,
				y: (row + Math.floor(indexInRank / maxNodesPerRow)) * RANK_SPACING_Y,
			});
		}
		row += Math.ceil(ordered.length / maxNodesPerRow);
	}
	return positions;
}
