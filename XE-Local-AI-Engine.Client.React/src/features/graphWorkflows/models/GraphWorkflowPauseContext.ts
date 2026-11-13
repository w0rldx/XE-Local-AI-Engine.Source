// The `context` edges the editor draws around a Pause on the connect gesture. Pure: no React.

import { mintEdgeKey } from "@/features/graphWorkflows/models/GraphWorkflowAuthoring";
import type {
	GraphWorkflowCanvasEdge,
	GraphWorkflowCanvasNode,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

/** One directed pair of node keys, which is all the two walks below read off an edge. */
interface GraphWorkflowWire {
	readonly from: string;
	readonly to: string;
}

/**
 * The nodes a Pause's content really comes from: its predecessors, walking THROUGH consecutive Pause nodes, because a
 * Pause's own output is the approval and not the answer. The seen-set stops a damaged graph that loops a Pause back
 * into itself. `Start` is a fine answer — its output is the run's input, which is exactly what a node behind the Pause
 * would otherwise have read. Mirrors `CanvasWorkflowImport.NonPauseAncestors`.
 */
function nonPauseAncestors(pause: string, pauseKeys: ReadonlySet<string>, wiring: readonly GraphWorkflowWire[]): string[] {
	const resolved: string[] = [];
	const seen = new Set<string>([pause]);
	const pending = [pause];
	while (pending.length > 0) {
		const current = pending.pop() ?? "";
		for (const predecessor of wiring.filter((pair) => pair.to === current).map((pair) => pair.from)) {
			// `Set.add` answers the SET, not "was it new" — the check has to be `has`, or a Pause wired back into itself
			// re-enters the walk forever.
			if (seen.has(predecessor)) {
				continue;
			}
			seen.add(predecessor);
			if (pauseKeys.has(predecessor)) {
				pending.push(predecessor);
			} else {
				resolved.push(predecessor);
			}
		}
	}
	return resolved.toSorted((left, right) => left.localeCompare(right));
}

/**
 * Every node a wire INTO `pause` can starve: the pause's own successors, and — walking THROUGH a successor that is
 * itself a Pause, whose output is only its approval — the nodes behind that one too. The forward mirror of
 * `nonPauseAncestors`, and what keeps `A → P1 → P2 → B` whole: wiring `A → P1` has to reach `B`, not just `P2`.
 *
 * A Pause successor is a candidate as well as a node to walk through: `P2` needs the content it is approving as much
 * as `B` does. Candidates only — whether each one is actually starved is decided per successor, on its own inbound
 * edges and its own ancestry, never on the ancestry of the pause the walk happened to arrive from.
 */
function successorsThroughPauses(pause: string, pauseKeys: ReadonlySet<string>, wiring: readonly GraphWorkflowWire[]): string[] {
	const resolved: string[] = [];
	const seen = new Set<string>([pause]);
	const pending = [pause];
	while (pending.length > 0) {
		const current = pending.pop() ?? "";
		for (const successor of wiring.filter((pair) => pair.from === current).map((pair) => pair.to)) {
			if (seen.has(successor)) {
				continue;
			}
			seen.add(successor);
			resolved.push(successor);
			if (pauseKeys.has(successor)) {
				pending.push(successor);
			}
		}
	}
	return resolved;
}

/**
 * The `context` edges this graph is missing around its Pause nodes — the authoring half of the affordance the importer
 * has (`CanvasWorkflowImport.AddPauseContextEdges`, wiki page 21 §4.6).
 *
 * A Pause writes `{decision, comment, payload}` and a node's `input` is its ONE satisfied predecessor's output, so an
 * authored `X → Pause → Y` hands Y the approval and never X's answer. One unconditional edge from the Pause's nearest
 * NON-Pause ancestor to Y fixes that: with two satisfied predecessors and the default `All` join policy, Y is admitted
 * only once both the content and the approval have arrived, and its `input` is the `upstream` map carrying both.
 *
 * Keyed on the SUCCESSOR, and the rule is `GraphWorkflowGraph.PauseContextWarnings` — the same graph must not be told
 * by the validator that nothing can be named for Y while this pass silently draws an edge into it. So, per candidate:
 *   - every inbound edge of Y must leave a Pause. A Y something else also feeds already has its content, and the
 *     unconditional edge would only add a branch its `All` policy has to wait for;
 *   - the ancestors are the union over EVERY pause that feeds Y, walked back through consecutive pauses. Exactly one
 *     candidate, or nothing is drawn: two means the pauses are fed by mutually exclusive branches, and an `All` node
 *     with a dead inbound edge is SKIPPED (`GraphWorkflowStateMachine.Admission`), so the affordance would delete the
 *     node it was meant to feed. It is Y's own ancestry that decides, never that of the pause a walk arrived from;
 *   - a `Condition` ancestor is not named, which the importer never meets: the added edge would carry no
 *     `sourceHandle`, so it saves as a second UNCONDITIONAL out-edge of that Condition, which both this client's
 *     `conditionMultipleDefaults` rule and the server's own parser refuse;
 *   - a Y whose `joinPolicy` is `Any` is skipped. `Any` fires on ONE satisfied branch, so an unconditional content
 *     edge stays satisfied when every approval is rejected and Y would run past the Pause it was waiting on. The
 *     policy is read off the NODE whatever its kind: `Admission` honors it on an `End` or an `Agent` too, and
 *     reading it off `Join` alone is the documented trap.
 * A self-loop is never drawn, which is the one guard left over from the importer.
 *
 * Returns only the edges to ADD, so the caller can tell the operator that something appeared on the canvas. PURE — the
 * editor runs it on the connect gesture and nowhere else, so an edge the operator deletes stays deleted.
 *
 * `connection` narrows the pass to what that ONE gesture can have broken, which is what keeps the editor from arguing
 * with an operator who deleted an edge on purpose:
 *   - wiring INTO a Pause can starve any of its successors, so all of them are considered;
 *   - wiring OUT OF a Pause can only starve the node just connected, so only that target is.
 * Omitted, every Pause and every successor is considered — the whole-graph pass the unit tests read.
 */
export function pauseContextEdges(
	nodes: readonly GraphWorkflowCanvasNode[],
	edges: readonly GraphWorkflowCanvasEdge[],
	connection?: { readonly from: string; readonly to: string },
): readonly GraphWorkflowCanvasEdge[] {
	const kindByKey = new Map(nodes.map((node) => [node.id, node.data.kind]));
	// A ChatInput parks like a Pause and hands on only its answer, so it is walked exactly as one.
	const pauseKeys = new Set(
		nodes.filter((node) => node.data.kind === "Pause" || node.data.kind === "ChatInput").map((node) => node.id),
	);
	// EVERY kind, not just `Join`: the policy is a member of the node base and the run reads it off whichever node it
	// is admitting. An `End` or an `Agent` set to `Any` joins exactly as a `Join` does.
	// A DevTask `apply` is skipped as well, as `PauseContextWarnings` exempts it: DEVTASK-1 refuses the very content
	// edge this pass would draw, because every way into an apply must be a Pause's Approve.
	const skipped = new Set(
		nodes
			.filter((node) => node.data.joinPolicy === "Any" || (node.data.kind === "DevTask" && node.data.action === "apply"))
			.map((node) => node.id),
	);
	if (pauseKeys.size === 0) {
		return [];
	}
	// A snapshot: every walk reads the graph as the operator wired it, never the edges this pass adds to it.
	const wiring: readonly GraphWorkflowWire[] = edges.map((edge) => ({ from: edge.source, to: edge.target }));
	const successorsOf = (pause: string): readonly string[] => [
		...new Set(wiring.filter((pair) => pair.from === pause).map((pair) => pair.to)),
	];

	const byKey = (left: string, right: string) => left.localeCompare(right);
	const candidates =
		connection === undefined
			? [...pauseKeys].flatMap((pause) => [...successorsOf(pause)])
			: [
					// Through consecutive Pause nodes, because the whole-graph pass is not coming to visit the second one:
					// with `P1 → P2 → B` already drawn, wiring `A → P1` is the only gesture `B` will ever get.
					...(pauseKeys.has(connection.to) ? successorsThroughPauses(connection.to, pauseKeys, wiring) : []),
					...(pauseKeys.has(connection.from) ? [connection.to] : []),
				];

	const taken = new Set([...nodes.map((node) => node.id), ...edges.map((edge) => edge.id)]);
	const added: GraphWorkflowCanvasEdge[] = [];

	for (const successor of [...new Set(candidates)].toSorted(byKey)) {
		const inbound = wiring.filter((pair) => pair.to === successor);
		if (!inbound.every((pair) => pauseKeys.has(pair.from)) || skipped.has(successor)) {
			continue;
		}
		// The union over every pause that feeds this successor, so a second pause carrying a second branch is counted.
		// Exactly one candidate, or nothing is drawn — see the guards above.
		const ancestors = new Set(inbound.flatMap((pair) => nonPauseAncestors(pair.from, pauseKeys, wiring)));
		const [ancestor] = ancestors;
		if (ancestors.size !== 1 || ancestor === undefined || ancestor === successor || kindByKey.get(ancestor) === "Condition") {
			continue;
		}
		const key = mintEdgeKey(taken);
		taken.add(key);
		// Both labels, for the same reason `graphToCanvas` writes both: React Flow renders the top-level one.
		added.push({
			id: key,
			source: ancestor,
			target: successor,
			label: "context",
			data: { label: "context" },
		});
	}
	return added;
}
