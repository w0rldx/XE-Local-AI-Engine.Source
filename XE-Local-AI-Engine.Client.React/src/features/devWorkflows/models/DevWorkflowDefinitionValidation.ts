// The client half of the definition editor's save gate: the graph rules the FORM can actually break, checked before
// the PUT so the operator is told which node is wrong instead of reading a 400 that names none.
//
// This mirrors a deliberate SUBSET of `DevWorkflowGraph.ValidateAndCountNodes` (P4 §2.9, D row) — the rules that
// govern the fields the form edits. `toolMode` and `materialization` are round-tripped untouched and never authored
// here, so their own server rules (apply-gating, template nesting, positive `maxChildren`) are deliberately NOT
// mirrored: a check over a field the form cannot change could only ever fail on a graph the server already accepted.
// There is no linearity validator either — that is Preview's invariant, not this one's.
//
// The four C4 invariant rules ARE mirrored, because the form now authors both fields they read: `requiredCapabilities`
// on an Agent node and the graph's own `allowUngatedWrites`. Editing an edge condition or deleting a node breaks the
// other two. Each is the same question `DevWorkflowGraph` asks, phrased over the wire graph — including the shared
// `assured` fixpoint, which is keyed on `joinPolicy` and never on node type: keying it on `Join` rejects the shipped
// template, whose verification node is an Agent with two inbound edges.
//
// Every rule below is the same question the server asks, phrased over the WIRE graph. Where the two could drift the
// client is the one that must give way, which is why a save still renders whatever the server refuses.

import {
	type DevWorkflowGraph,
	type DevWorkflowGraphEdge,
	type DevWorkflowGraphNode,
	isDevWorkflowApplyToolMode,
} from "@/features/devWorkflows/models/DevWorkflowModels";

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
	"deadGateEdge",
	"strandedBranch",
	"ungatedWrite",
	"applyWithoutValidation",
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
 * The node keys of every materialization TEMPLATE subtree, derived from the graph BEING EDITED.
 *
 * Mirrors `DevWorkflowGraph.TemplateSubtree`: each declared `materialization.templateNodeKey` plus everything
 * reachable from it WITHOUT passing through that materialization's `joinNodeKey`, because the join is where a template
 * hands its work back to the graph. Each materialization gets its own visited set, like the server's, so two templates
 * that overlap still declare their own subtrees in full.
 *
 * Derived here rather than read off the wire's `isTemplate`: that flag is the server's verdict over the document as
 * LOADED, and this validator judges the document as EDITED. A node added inside a subtree carries no flag and read as
 * an orphan; a materializing node the operator deleted left its children flagged as templates that no longer are.
 * `isTemplate` remains what the run views display, where the loaded document is the only one there is.
 */
function templateNodeKeys(
	nodes: readonly DevWorkflowGraphNode[],
	successors: ReadonlyMap<string, readonly string[]>,
): ReadonlySet<string> {
	const templates = new Set<string>();
	for (const node of nodes) {
		const root = node.materialization?.templateNodeKey;
		if (!root) {
			continue;
		}
		const join = node.materialization?.joinNodeKey;
		const subtree = new Set<string>([root]);
		const pending = [root];
		while (pending.length > 0) {
			const from = pending.pop() ?? "";
			for (const to of successors.get(from) ?? []) {
				if (to === join || subtree.has(to)) {
					continue;
				}
				subtree.add(to);
				pending.push(to);
			}
		}
		for (const key of subtree) {
			templates.add(key);
		}
	}
	return templates;
}

/**
 * The effect vocabulary, verbatim from `DevWorkflowNodeEffect`. A capability key outside it is a server 400, so the
 * capability editor offers exactly these four and nothing else.
 */
export const devWorkflowNodeEffects = ["ReadLocal", "WriteExecute", "Orchestration", "Network"] as const;
export type DevWorkflowNodeEffect = (typeof devWorkflowNodeEffects)[number];

/** `DevWorkflowGraph.MaxCapabilityReasonLength`. A one-line justification, not the node's instructions. */
export const devWorkflowCapabilityReasonMaxLength = 200;

/**
 * What a node can change — the mirror of `DevWorkflowGraph.Effects`, DECLARED for an Agent and derived for every other
 * node type.
 *
 * Derived here rather than read off `DevWorkflowGraphContract.EffectsOf`, for the same reason `templateNodeKeys` is:
 * the server's answer is over the document as STORED and this editor judges the document as EDITED. A node whose type
 * the operator just changed would otherwise wear the badges of the node it used to be, and the `ungatedWrite` rule
 * below would refuse — or fail to refuse — on a graph nobody is looking at any more.
 */
export function devWorkflowEffectsOf(node: DevWorkflowGraphNode): readonly DevWorkflowNodeEffect[] {
	switch (node.nodeType ?? "Agent") {
		case "Agent":
			// Case-insensitively, as the parser reads them; an unknown key is the parser's own 400 and not an effect.
			return devWorkflowNodeEffects.filter((effect) =>
				Object.keys(node.requiredCapabilities ?? {}).some((key) => key.toLowerCase() === effect.toLowerCase()),
			);
		case "DevTask":
			return ["WriteExecute"];
		case "Tool": {
			if (isDevWorkflowApplyToolMode(node.toolMode)) {
				return ["WriteExecute"];
			}
			// A validation naming no command inherits the project profile's set, which is not knowable here — so the
			// answer fails toward the WIDER set rather than guessing the narrower one, exactly as the parser does.
			const commands = node.validationCommandIds ?? [];
			return commands.length === 0 || commands.includes("dotnet_restore") ? ["ReadLocal", "Network"] : ["ReadLocal"];
		}
		default:
			return [];
	}
}

/**
 * How far a node's write reaches (`DevWorkflowGraph.ScopeOf`): a DevTask writes a worktree under this node's own data
 * root and its patch reaches a real repository only through an apply node, so it is not what the gate rule is about.
 */
function writesTheRepository(node: DevWorkflowGraphNode): boolean {
	return (node.nodeType ?? "Agent") !== "DevTask" && devWorkflowEffectsOf(node).includes("WriteExecute");
}

/**
 * The authored edges plus one VIRTUAL edge from each materializing node to its template root — `AugmentedEdges`. The
 * materializer wires exactly that edge at run time, so the fixpoint below gives the same answer before and after
 * materialization. A self-edge is skipped: a node may name itself as its own template root.
 */
function augmentedEdges(
	nodes: readonly DevWorkflowGraphNode[],
	edges: readonly { readonly from: string; readonly to: string }[],
): readonly { readonly from: string; readonly to: string }[] {
	return [
		...edges,
		...nodes
			.filter((node) => node.materialization?.templateNodeKey)
			.map((node) => ({ from: node.nodeKey ?? "", to: node.materialization?.templateNodeKey ?? "" }))
			.filter((edge) => edge.from !== edge.to && edge.from.length > 0 && edge.to.length > 0),
	];
}

/**
 * "Has EVERY run that reaches this node already passed a node with this property?" — the mirror of
 * `DevWorkflowGraph.Assured`, and the one dataflow both C4-2 and C4-3 ask.
 *
 * `Assured(v) = P(v) || Combine(inbound of v)` with `Combine(∅) = false`, so an entry node evaluates to `P(entry)` and
 * is NOT initialised false — a definition whose entry IS the gate, or IS the validation, is a valid shape. `Combine` is
 * keyed on `joinPolicy`: OR for `All` (every branch completes, so one carrying the property is enough) and AND for
 * `Any` (only one branch may have run). Keying it on node TYPE rejects `feature-development-v1`.
 */
function assured(
	nodes: readonly DevWorkflowGraphNode[],
	edges: readonly { readonly from: string; readonly to: string }[],
	property: (node: DevWorkflowGraphNode) => boolean,
): ReadonlySet<string> {
	const byKey = new Map(nodes.map((node) => [node.nodeKey ?? "", node]));
	const inbound = new Map<string, string[]>();
	for (const edge of edges) {
		inbound.set(edge.to, [...(inbound.get(edge.to) ?? []), edge.from]);
	}

	// Ancestors first, so one pass settles the fixpoint. Safe because the caller runs this only on an acyclic graph.
	const order: string[] = [];
	const placed = new Set<string>();
	const place = (key: string): void => {
		if (placed.has(key)) {
			return;
		}
		placed.add(key);
		for (const from of inbound.get(key) ?? []) {
			place(from);
		}
		order.push(key);
	};
	for (const key of byKey.keys()) {
		place(key);
	}

	const settled = new Set<string>();
	for (const key of order) {
		const node = byKey.get(key);
		if (!node) {
			continue;
		}
		const sources = inbound.get(key) ?? [];
		const combined =
			sources.length > 0 &&
			((node.joinPolicy ?? "All").toLowerCase() === "any"
				? sources.every((from) => settled.has(from))
				: sources.some((from) => settled.has(from)));
		if (property(node) || combined) {
			settled.add(key);
		}
	}
	return settled;
}

/**
 * Whether one edge condition accepts one output document — the mirror of `DevWorkflowCondition.Evaluate`, including its
 * fail-closed reading: a path the document does not carry answers false for every operator but `notExists`.
 */
function conditionFires(condition: DevWorkflowGraphEdge["condition"], output: Record<string, unknown>): boolean {
	if (!condition?.path) {
		return true;
	}
	let resolved: unknown = output;
	for (const segment of condition.path.split(".")) {
		if (typeof resolved !== "object" || resolved === null || !(segment in resolved)) {
			resolved = undefined;
			break;
		}
		resolved = (resolved as Record<string, unknown>)[segment];
	}
	const op = (condition.op ?? "").toLowerCase();
	if (resolved === undefined) {
		return op === "notexists";
	}
	const value = condition.value;
	switch (op) {
		case "exists":
			return true;
		case "notexists":
			return false;
		case "eq":
			return resolved === value;
		case "ne":
			return resolved !== value;
		default:
			// The four relational operators order numbers numerically and strings ordinally; anything else, a type
			// mismatch included, is not an ordering and reads as "no".
			if (typeof resolved !== typeof value || (typeof resolved !== "number" && typeof resolved !== "string")) {
				return false;
			}
			return op === "gt"
				? resolved > (value as typeof resolved)
				: op === "gte"
					? resolved >= (value as typeof resolved)
					: op === "lt"
						? resolved < (value as typeof resolved)
						: resolved <= (value as typeof resolved);
	}
}

/**
 * The out-edges of a human gate that no answer would take — `DeadGateEdges`, asked of the document a gate really
 * produces for each of the three answers it succeeds on rather than by reading the condition's text.
 */
function deadGateEdges(
	nodes: readonly DevWorkflowGraphNode[],
	edges: readonly DevWorkflowGraphEdge[],
): readonly DevWorkflowGraphEdge[] {
	const gates = new Set(nodes.filter((node) => node.nodeType === "HumanGate").map((node) => node.nodeKey ?? ""));
	return edges.filter(
		(edge) =>
			gates.has(edge.from ?? "") &&
			!["Approve", "Reject", "RequestChanges"].some((decision) =>
				conditionFires(edge.condition, { status: "succeeded", decision }),
			),
	);
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
	// children are materialized, so it is neither an entry nor an orphan.
	const templates = templateNodeKeys(nodes, successors);
	const entries = [...declared].filter((key) => (inboundCount.get(key) ?? 0) === 0 && !templates.has(key));
	if (declared.size > 0 && entries.length === 0) {
		issues.push({ rule: "noEntry", subject: "" });
	}
	for (const extra of entries.slice(1)) {
		issues.push({ rule: "multipleEntries", subject: extra });
	}

	const cyclic = hasCycle([...declared], successors);
	if (cyclic) {
		// The message says what to do instead, because the shape an author reaches for here is a fix loop and the
		// runtime models one: a `retryTarget` routes back without the graph carrying a back edge.
		issues.push({ rule: "cycle", subject: "" });
	}

	const entry = entries[0];
	if (entry !== undefined) {
		const reachable = new Set([entry, ...descendants(entry, successors)]);
		for (const key of declared) {
			if (!reachable.has(key) && !templates.has(key)) {
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

	issues.push(...capabilityInvariants(graph, nodes, edges, templates, cyclic));
	return issues;
}

/**
 * The four C4 invariants, over the same augmented edge set the server uses. Skipped wholesale on a cyclic graph: the
 * fixpoint below assumes a topological order, and the `cycle` issue already refuses the save.
 */
function capabilityInvariants(
	graph: DevWorkflowGraph,
	nodes: readonly DevWorkflowGraphNode[],
	edges: readonly { readonly from: string; readonly to: string }[],
	templates: ReadonlySet<string>,
	cyclic: boolean,
): readonly DevWorkflowGraphIssue[] {
	if (cyclic) {
		return [];
	}
	const issues: DevWorkflowGraphIssue[] = [];
	const augmented = augmentedEdges(nodes, edges);

	// GRAPH-C4-1, in the server's own order: the gates that own a dead edge are named first, because a chain of gates
	// strands every gate above the broken one and naming those sends the operator to fix the wrong line.
	const dead = deadGateEdges(nodes, graph.edges ?? []);
	if (dead.length > 0) {
		const live = augmented.filter(
			(edge) => !dead.some((deadEdge) => (deadEdge.from ?? "") === edge.from && (deadEdge.to ?? "") === edge.to),
		);
		const terminals = new Set(
			nodes.map((node) => node.nodeKey ?? "").filter((key) => !edges.some((edge) => edge.from === key)),
		);
		const reachesAnEnd = new Set(terminals);
		const pending = [...terminals];
		while (pending.length > 0) {
			const current = pending.pop() ?? "";
			for (const edge of live.filter((candidate) => candidate.to === current)) {
				if (!reachesAnEnd.has(edge.from)) {
					reachesAnEnd.add(edge.from);
					pending.push(edge.from);
				}
			}
		}
		const culprits = new Set(dead.map((edge) => edge.from ?? ""));
		const stranded = nodes
			.map((node) => node.nodeKey ?? "")
			.filter((key) => !templates.has(key) && !reachesAnEnd.has(key))
			.toSorted((left, right) => Number(culprits.has(right)) - Number(culprits.has(left)) || left.localeCompare(right))
			.at(0);
		if (stranded !== undefined) {
			issues.push({ rule: culprits.has(stranded) ? "deadGateEdge" : "strandedBranch", subject: stranded });
		} else {
			const orphaned = dead.toSorted(
				(left, right) =>
					(left.from ?? "").localeCompare(right.from ?? "") || (left.to ?? "").localeCompare(right.to ?? ""),
			)[0];
			issues.push({ rule: "deadGateEdge", subject: `${orphaned?.from ?? ""} → ${orphaned?.to ?? ""}` });
		}
	}

	// GRAPH-C4-2: a node that writes outside its sandbox is reached through a human gate, unless the template says
	// once and in writing that it need not be.
	if (graph.allowUngatedWrites !== true) {
		const gated = assured(nodes, augmented, (node) => node.nodeType === "HumanGate");
		const ungated = nodes.find((node) => writesTheRepository(node) && !gated.has(node.nodeKey ?? ""));
		if (ungated) {
			issues.push({ rule: "ungatedWrite", subject: ungated.nodeKey ?? "" });
		}
	}

	// GRAPH-C4-3, the structural half: every path into an apply node passes a Tool node in Validate mode.
	const applies = nodes.filter((node) => isDevWorkflowApplyToolMode(node.toolMode));
	if (applies.length > 0) {
		const validated = assured(
			nodes,
			augmented,
			(node) => node.nodeType === "Tool" && !isDevWorkflowApplyToolMode(node.toolMode),
		);
		const unvalidated = applies.find((node) => !validated.has(node.nodeKey ?? ""));
		if (unvalidated) {
			issues.push({ rule: "applyWithoutValidation", subject: unvalidated.nodeKey ?? "" });
		}
	}

	return issues;
}
