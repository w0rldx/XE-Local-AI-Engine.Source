// The client half of the definition editor's save gate: the graph rules the FORM can actually break, checked before
// the PUT so the operator is told which node is wrong instead of reading a 400 that names none.
//
// This mirrors a deliberate SUBSET of `DevWorkflowGraph.ValidateAndCountNodes` (P4 §2.9, D row) — the rules that
// govern the fields the form edits. `toolMode`, `materialization` and `requiredCapabilities` are round-tripped
// untouched and never authored here, so their server rules (apply-gating, template nesting, positive `maxChildren`)
// are deliberately NOT mirrored: a check over a field the form cannot change could only ever fail on a graph the
// server already accepted. There is no linearity validator either — that is Preview's invariant, not this one's.
//
// Every rule below is the same question the server asks, phrased over the WIRE graph. Where the two could drift the
// client is the one that must give way, which is why a save still renders whatever the server refuses.

import type { DevWorkflowGraph } from "@/features/devWorkflows/models/DevWorkflowModels";

/** One rule, one i18n key. The rule name IS the key suffix, so a new rule cannot ship without a message. */
export const devWorkflowGraphRules = [
	"duplicateNodeKey",
	"missingNodeKey",
	"unknownEdgeEndpoint",
	"duplicateEdge",
	"noEntry",
	"multipleEntries",
	"cycle",
	"orphan",
	"unknownRetryTarget",
	"retryTargetNotAncestor",
	"joinAnyNeedsTwoInbound",
] as const;
export type DevWorkflowGraphRule = (typeof devWorkflowGraphRules)[number];

export interface DevWorkflowGraphIssue {
	readonly rule: DevWorkflowGraphRule;
	/** The node or edge the rule is about, for the message and for pointing the operator at the row. */
	readonly subject: string;
}

/** Edges the graph actually declares, with both endpoints as plain strings. */
function edgePairs(graph: DevWorkflowGraph): readonly { readonly from: string; readonly to: string }[] {
	return (graph.edges ?? []).map((edge) => ({ from: edge.from ?? "", to: edge.to ?? "" }));
}

/** Every node key reachable from `start` by walking edges forward. Used for the entry reach and for ancestry. */
function descendants(start: string, successors: ReadonlyMap<string, readonly string[]>): ReadonlySet<string> {
	const seen = new Set<string>();
	const pending = [start];
	while (pending.length > 0) {
		const current = pending.pop();
		for (const next of successors.get(current ?? "") ?? []) {
			if (!seen.has(next)) {
				seen.add(next);
				pending.push(next);
			}
		}
	}
	return seen;
}

/**
 * True when the graph has a cycle. Iterative depth-first with a colour map, because a workflow authored by hand can
 * be deep and a recursive walk would put an editor bug on the stack rather than in the issue list.
 */
function hasCycle(nodeKeys: readonly string[], successors: ReadonlyMap<string, readonly string[]>): boolean {
	const state = new Map<string, "open" | "done">();
	for (const root of nodeKeys) {
		if (state.has(root)) {
			continue;
		}
		const stack: { readonly key: string; readonly entering: boolean }[] = [{ key: root, entering: true }];
		while (stack.length > 0) {
			const frame = stack.pop();
			if (!frame) {
				continue;
			}
			if (!frame.entering) {
				state.set(frame.key, "done");
				continue;
			}
			if (state.get(frame.key) === "open") {
				return true;
			}
			if (state.get(frame.key) === "done") {
				continue;
			}
			state.set(frame.key, "open");
			stack.push({ key: frame.key, entering: false });
			for (const next of successors.get(frame.key) ?? []) {
				if (state.get(next) === "open") {
					return true;
				}
				if (state.get(next) !== "done") {
					stack.push({ key: next, entering: true });
				}
			}
		}
	}
	return false;
}

/**
 * Everything wrong with this graph that the form could have caused, in a stable order. An empty list means the save
 * is worth attempting — not that the server will accept it.
 */
export function validateDevWorkflowGraph(graph: DevWorkflowGraph | undefined): readonly DevWorkflowGraphIssue[] {
	if (!graph) {
		return [];
	}
	const issues: DevWorkflowGraphIssue[] = [];
	const nodes = graph.nodes ?? [];
	const nodeKeys = nodes.map((node) => node.nodeKey ?? "");
	const declared = new Set(nodeKeys.filter((key) => key.length > 0));

	const seenKeys = new Set<string>();
	for (const key of nodeKeys) {
		if (key.length === 0) {
			issues.push({ rule: "missingNodeKey", subject: "" });
			continue;
		}
		if (seenKeys.has(key)) {
			issues.push({ rule: "duplicateNodeKey", subject: key });
		}
		seenKeys.add(key);
	}

	const edges = edgePairs(graph);
	const seenEdges = new Set<string>();
	const successors = new Map<string, string[]>();
	const inboundCount = new Map<string, number>();
	for (const edge of edges) {
		const label = `${edge.from} → ${edge.to}`;
		if (!declared.has(edge.from) || !declared.has(edge.to)) {
			issues.push({ rule: "unknownEdgeEndpoint", subject: label });
			continue;
		}
		if (seenEdges.has(label)) {
			issues.push({ rule: "duplicateEdge", subject: label });
			continue;
		}
		seenEdges.add(label);
		successors.set(edge.from, [...(successors.get(edge.from) ?? []), edge.to]);
		inboundCount.set(edge.to, (inboundCount.get(edge.to) ?? 0) + 1);
	}

	// A materialization TEMPLATE has no inbound edge from outside its own subtree and gets no node run until its
	// children are materialized, so it is neither an entry nor an orphan. `isTemplate` is the server's own
	// TemplateSubtree verdict on this document; a client walk of the same rule is the drift this replaced.
	const isTemplate = new Map(nodes.map((node) => [node.nodeKey ?? "", node.isTemplate === true]));
	const entries = [...declared].filter((key) => (inboundCount.get(key) ?? 0) === 0 && isTemplate.get(key) !== true);
	if (declared.size > 0 && entries.length === 0) {
		issues.push({ rule: "noEntry", subject: "" });
	}
	for (const extra of entries.slice(1)) {
		issues.push({ rule: "multipleEntries", subject: extra });
	}

	if (hasCycle([...declared], successors)) {
		// The message says what to do instead, because the shape an author reaches for here is a fix loop and the
		// runtime models one: a `retryTarget` routes back without the graph carrying a back edge.
		issues.push({ rule: "cycle", subject: "" });
	}

	const entry = entries[0];
	if (entry !== undefined) {
		const reachable = new Set([entry, ...descendants(entry, successors)]);
		for (const key of declared) {
			if (!reachable.has(key) && isTemplate.get(key) !== true) {
				issues.push({ rule: "orphan", subject: key });
			}
		}
	}

	for (const node of nodes) {
		const key = node.nodeKey ?? "";
		const retryTarget = node.retryTarget ?? "";
		if (retryTarget.length > 0) {
			if (!declared.has(retryTarget)) {
				issues.push({ rule: "unknownRetryTarget", subject: key });
			} else if (!descendants(retryTarget, successors).has(key)) {
				// The target has to be UPSTREAM: a fix loop re-runs the work this node depends on. Routing to a node
				// that cannot reach this one restarts a branch that was never going to produce what this node failed on.
				issues.push({ rule: "retryTargetNotAncestor", subject: key });
			}
		}
		if ((node.joinPolicy ?? "").toLowerCase() === "any" && (inboundCount.get(key) ?? 0) < 2) {
			issues.push({ rule: "joinAnyNeedsTwoInbound", subject: key });
		}
	}

	return issues;
}
