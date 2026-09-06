// The client half of the graph editor's save gate, plus the per-form Zod schemas the config drawer runs at field level.
//
// `validateGraphWorkflowGraph` mirrors a deliberate SUBSET of the server's graph parser (brief §3.1) — the rules the
// canvas can actually break — so the operator is told which node is wrong instead of reading a 400 that names none.
// THE SERVER WINS on any disagreement: this list is advisory, a save still renders whatever the server refuses, and
// `serverErrorsToIssues` maps the server's own `errors[]` into the same shape so one component draws both.
//
// Deliberately NOT mirrored, because the client cannot answer them honestly:
//   - whether a Tool node's `toolName` resolves to a `ReadLocal`, no-approval tool (D6). `GET graph-workflows/tools`
//     is already filtered server-side; re-deriving eligibility in TypeScript would be a second, drifting rule.
//   - whether an Agent node's effective model is a cloud model (refused at run start, ruling R1) — that is a
//     resolution over agent definitions and installed runtimes, not over the graph.
//   - `responseJsonSchema` being a valid JSON *schema*. "Parses as an object" is checked; the rest is the server's.
//
// Two mechanisms, deliberately: the structural rules above are pure functions over the WIRE graph and gate Save, while
// the Zod schemas below run per field in the drawer and only produce a field message. Their messages are full i18n
// KEYS, so a form renders `t(issue.message)` without a lookup table of its own.

import { z } from "zod";

import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	GRAPH_WORKFLOW_MAX_NODES,
	type GraphWorkflowGraph,
	type GraphWorkflowGraphEdge,
	type GraphWorkflowGraphNode,
	type GraphWorkflowNodeKind,
	type GraphWorkflowValidationErrorResponse,
	graphWorkflowConditionOperators,
	graphWorkflowNodeKinds,
	narrowGraphWorkflowJoinPolicy,
	normalizeGraphWorkflowConditionOperator,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/**
 * One rule, one i18n key: the member name IS the suffix under `pages.graphWorkflows.definition.issues`, so a new rule
 * cannot ship without a message (`I18nParity.test.ts` asserts the whole array).
 *
 * `serverRejected` is the one member no client check produces — it carries the server's own sentence through the same
 * shape, so the validation strip has one render path rather than two.
 */
export const graphWorkflowGraphRules = [
	"duplicateNodeKey",
	"missingNodeKey",
	"invalidNodeKey",
	"unknownEdgeEndpoint",
	"duplicateEdgeKey",
	"parallelEdgesBothUnconditional",
	"conditionEdgeHasNoPath",
	"noStart",
	"multipleStarts",
	"noEnd",
	"cycle",
	"unreachable",
	"danglingNonEnd",
	"endHasOutbound",
	"startHasInbound",
	"joinAnyNeedsTwoInbound",
	"conditionNeedsTwoOutbound",
	"conditionMultipleDefaults",
	"tooManyNodes",
	"invalidJson",
	"pauseDecisionUnroutable",
	"unknownNodeKind",
	"unknownConditionOperator",
	"toolNameMissing",
	"serverRejected",
] as const;
export type GraphWorkflowGraphRule = (typeof graphWorkflowGraphRules)[number];

export interface GraphWorkflowGraphIssue {
	readonly rule: GraphWorkflowGraphRule;
	/** The node or edge key the rule is about — one namespace, so it is never ambiguous which it points at. */
	readonly subject?: string;
	/** The server's own text, for `serverRejected`. Client rules carry none: their message is the i18n key. */
	readonly message?: string;
}

/** The wire's `config` as a bag. Anything that is not a plain object reads as empty, so a malformed node still renders. */
function configRecord(config: unknown): Record<string, unknown> {
	return typeof config === "object" && config !== null && !Array.isArray(config) ? (config as Record<string, unknown>) : {};
}

function isPlainObject(value: unknown): boolean {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function text(value: unknown): string {
	return typeof value === "string" ? value : "";
}

function isNodeKind(value: string | null | undefined): value is GraphWorkflowNodeKind {
	return graphWorkflowNodeKinds.includes(value as GraphWorkflowNodeKind);
}

/** The key an issue points at for an edge: its own key, falling back to the pair when the edge has none. */
function edgeSubject(edge: GraphWorkflowGraphEdge): string {
	const key = edge.key ?? "";
	return key.length > 0 ? key : `${edge.from ?? ""} → ${edge.to ?? ""}`;
}

function hasCondition(edge: GraphWorkflowGraphEdge): boolean {
	return edge.condition !== undefined && edge.condition !== null;
}

/**
 * True when the graph has a cycle. Iterative depth-first with a colour map, because a graph authored by hand can be
 * deep and a recursive walk would put an editor bug on the stack rather than in the issue list.
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

/** Every node key reachable from `start` by walking edges forward. */
function descendants(start: string, successors: ReadonlyMap<string, readonly string[]>): ReadonlySet<string> {
	const seen = new Set<string>();
	const pending = [start];
	while (pending.length > 0) {
		const current = pending.pop() ?? "";
		for (const next of successors.get(current) ?? []) {
			if (!seen.has(next)) {
				seen.add(next);
				pending.push(next);
			}
		}
	}
	return seen;
}

/** The JSON-shaped members each kind must carry as an OBJECT on the wire, and the field name for the message. */
function jsonObjectMembers(kind: GraphWorkflowNodeKind): readonly string[] {
	switch (kind) {
		case "Start":
			return ["inputSchema"];
		case "Agent":
			return ["responseJsonSchema"];
		case "Tool":
			return ["arguments"];
		default:
			return [];
	}
}

/** Node-level rules: keys, kinds, and the config members whose SHAPE the server enforces. */
function nodeIssues(nodes: readonly GraphWorkflowGraphNode[]): readonly GraphWorkflowGraphIssue[] {
	const issues: GraphWorkflowGraphIssue[] = [];
	const seen = new Set<string>();
	for (const node of nodes) {
		const key = node.key ?? "";
		if (key.length === 0) {
			issues.push({ rule: "missingNodeKey" });
		} else {
			if (!GRAPH_WORKFLOW_KEY_PATTERN.test(key)) {
				issues.push({ rule: "invalidNodeKey", subject: key });
			}
			if (seen.has(key)) {
				issues.push({ rule: "duplicateNodeKey", subject: key });
			}
			seen.add(key);
		}

		const kind = node.kind ?? "";
		if (!isNodeKind(kind)) {
			// A closed vocabulary (D6): the server refuses an unknown kind, and the canvas has no card to draw for it.
			issues.push({ rule: "unknownNodeKind", subject: key });
			continue;
		}

		const config = configRecord(node.config);
		for (const member of jsonObjectMembers(kind)) {
			const value = config[member];
			if (value !== undefined && value !== null && !isPlainObject(value)) {
				issues.push({ rule: "invalidJson", subject: key });
			}
		}
		if (kind === "Tool" && text(config["toolName"]).trim().length === 0) {
			issues.push({ rule: "toolNameMissing", subject: key });
		}
	}
	return issues;
}

/** Edge-level rules: keys (one namespace with the nodes), endpoints, operators, and the two path rules. */
function edgeIssues(
	edges: readonly GraphWorkflowGraphEdge[],
	nodeByKey: ReadonlyMap<string, GraphWorkflowGraphNode>,
): readonly GraphWorkflowGraphIssue[] {
	const issues: GraphWorkflowGraphIssue[] = [];
	const edgeKeys = new Set<string>();
	const unconditionalPairs = new Set<string>();
	for (const edge of edges) {
		const key = edge.key ?? "";
		if (edgeKeys.has(key) || nodeByKey.has(key)) {
			// One namespace for node and edge keys (brief §3.1), which is what keeps an issue's `subject` unambiguous.
			issues.push({ rule: "duplicateEdgeKey", subject: edgeSubject(edge) });
		}
		edgeKeys.add(key);

		const from = edge.from ?? "";
		const to = edge.to ?? "";
		const source = nodeByKey.get(from);
		if (source === undefined || !nodeByKey.has(to)) {
			issues.push({ rule: "unknownEdgeEndpoint", subject: edgeSubject(edge) });
			continue;
		}

		if (!hasCondition(edge)) {
			const pair = `${from}>${to}`;
			if (unconditionalPairs.has(pair)) {
				// Parallel edges are legal when their keys differ and at most ONE of them is unconditional: a second
				// unconditional edge over the same pair is a branch that can never be told from the first.
				issues.push({ rule: "parallelEdgesBothUnconditional", subject: edgeSubject(edge) });
			}
			unconditionalPairs.add(pair);
			continue;
		}

		const condition = edge.condition ?? {};
		if (normalizeGraphWorkflowConditionOperator(condition.op) === undefined) {
			issues.push({ rule: "unknownConditionOperator", subject: edgeSubject(edge) });
		}
		const sourceKind = source.kind ?? "";
		const inheritedPath = sourceKind === "Condition" ? text(configRecord(source.config)["path"]) : "";
		if (text(condition.path).trim().length === 0 && inheritedPath.trim().length === 0) {
			// An edge with no path of its own inherits its source Condition node's `config.path` (ruling C2); every
			// conditional edge must resolve a path from one of the two, `Exists`/`NotExists` included.
			issues.push({ rule: "conditionEdgeHasNoPath", subject: edgeSubject(edge) });
		}
	}
	return issues;
}

/** Does this out-edge of a Pause node route the given decision? Unconditional, or the `output.decision Eq` shape. */
function routesDecision(edge: GraphWorkflowGraphEdge, decision: string): boolean {
	if (!hasCondition(edge)) {
		return true;
	}
	const condition = edge.condition ?? {};
	return (
		text(condition.path) === "output.decision" &&
		normalizeGraphWorkflowConditionOperator(condition.op) === "Eq" &&
		condition.value === decision
	);
}

/** Shape rules that need the whole graph: reachability, degrees, joins, Condition fan-out and the Pause pre-flight. */
function shapeIssues(
	nodes: readonly GraphWorkflowGraphNode[],
	edges: readonly GraphWorkflowGraphEdge[],
	nodeByKey: ReadonlyMap<string, GraphWorkflowGraphNode>,
): readonly GraphWorkflowGraphIssue[] {
	const issues: GraphWorkflowGraphIssue[] = [];
	const joined = edges.filter((edge) => nodeByKey.has(edge.from ?? "") && nodeByKey.has(edge.to ?? ""));
	const successors = new Map<string, string[]>();
	const outbound = new Map<string, GraphWorkflowGraphEdge[]>();
	const inboundCount = new Map<string, number>();
	for (const edge of joined) {
		const from = edge.from ?? "";
		const to = edge.to ?? "";
		successors.set(from, [...(successors.get(from) ?? []), to]);
		outbound.set(from, [...(outbound.get(from) ?? []), edge]);
		inboundCount.set(to, (inboundCount.get(to) ?? 0) + 1);
	}

	const starts = nodes.filter((node) => node.kind === "Start");
	if (starts.length === 0) {
		issues.push({ rule: "noStart" });
	}
	for (const extra of starts.slice(1)) {
		issues.push({ rule: "multipleStarts", subject: extra.key ?? "" });
	}
	if (!nodes.some((node) => node.kind === "End")) {
		issues.push({ rule: "noEnd" });
	}

	const cyclic = hasCycle([...nodeByKey.keys()], successors);
	if (cyclic) {
		issues.push({ rule: "cycle" });
	}

	const entry = starts[0]?.key ?? "";
	if (entry.length > 0 && !cyclic) {
		// Skipped on a cyclic graph: the `cycle` issue already refuses the save, and every node of a cycle downstream of
		// Start is reachable, so the walk would only add noise about the nodes it could not drain.
		const reachable = new Set([entry, ...descendants(entry, successors)]);
		for (const key of nodeByKey.keys()) {
			if (!reachable.has(key)) {
				issues.push({ rule: "unreachable", subject: key });
			}
		}
	}

	for (const node of nodes) {
		const key = node.key ?? "";
		const kind = node.kind ?? "";
		const out = outbound.get(key) ?? [];
		const inbound = inboundCount.get(key) ?? 0;
		if (kind === "End") {
			if (out.length > 0) {
				issues.push({ rule: "endHasOutbound", subject: key });
			}
		} else if (out.length === 0) {
			issues.push({ rule: "danglingNonEnd", subject: key });
		}
		if (kind === "Start" && inbound > 0) {
			issues.push({ rule: "startHasInbound", subject: key });
		}
		if (narrowGraphWorkflowJoinPolicy(node.joinPolicy) === "Any" && inbound < 2) {
			issues.push({ rule: "joinAnyNeedsTwoInbound", subject: key });
		}
		if (kind === "Condition") {
			if (out.length < 2) {
				issues.push({ rule: "conditionNeedsTwoOutbound", subject: key });
			}
			if (out.filter((edge) => !hasCondition(edge)).length > 1) {
				issues.push({ rule: "conditionMultipleDefaults", subject: key });
			}
		}
		if (kind === "Pause") {
			const allowed = configRecord(node.config)["allowedDecisions"];
			const decisions = toGraphWorkflowDecisionKinds(
				Array.isArray(allowed) ? allowed.filter((entry): entry is string => typeof entry === "string") : [],
			);
			// The rule an operator breaks constantly: adding `Reject` to a Pause and never wiring it. One issue per
			// node rather than per decision — the fix is the same edge drawer either way.
			if (decisions.some((decision) => !out.some((edge) => routesDecision(edge, decision)))) {
				issues.push({ rule: "pauseDecisionUnroutable", subject: key });
			}
		}
	}
	return issues;
}

/**
 * Everything wrong with this graph that the canvas could have caused, in a stable order. An empty list means the save
 * is worth attempting — not that the server will accept it.
 */
export function validateGraphWorkflowGraph(graph: GraphWorkflowGraph | undefined): readonly GraphWorkflowGraphIssue[] {
	if (!graph) {
		// Nothing loaded is not a broken graph: the editor has no canvas to point an issue at yet. An empty NODE LIST is
		// a different thing and still reports `noStart`/`noEnd`.
		return [];
	}
	const nodes = graph.nodes ?? [];
	const edges = graph.edges ?? [];
	const issues: GraphWorkflowGraphIssue[] = [];
	if (nodes.length > GRAPH_WORKFLOW_MAX_NODES) {
		issues.push({ rule: "tooManyNodes" });
	}
	issues.push(...nodeIssues(nodes));

	// Keyed on the FIRST node with a key, so a duplicate key does not silently repoint every edge at the later node.
	const nodeByKey = new Map<string, GraphWorkflowGraphNode>();
	for (const node of nodes) {
		const key = node.key ?? "";
		if (key.length > 0 && !nodeByKey.has(key)) {
			nodeByKey.set(key, node);
		}
	}

	issues.push(...edgeIssues(edges, nodeByKey));
	issues.push(...shapeIssues(nodes, edges, nodeByKey));
	return issues;
}

/**
 * The server's `errors[]` in the client's shape (S0 ruled it hybrid: keyed failures accumulate, a malformed document
 * throws first as one unkeyed message). A keyed error attaches to its node or edge; an unkeyed one renders once above
 * the canvas. Same component, same path.
 */
export function serverErrorsToIssues(
	errors: readonly GraphWorkflowValidationErrorResponse[] | undefined,
): readonly GraphWorkflowGraphIssue[] {
	return (errors ?? []).map((error) => ({
		rule: "serverRejected" as const,
		subject: error.key ?? undefined,
		message: error.message,
	}));
}

// ---------------------------------------------------------------------------------------------------------------
// Zod schemas for the config drawer. Messages are full i18n keys: the form renders `t(issue.message)`.
// ---------------------------------------------------------------------------------------------------------------

/** Dot paths only — no wildcards, indexes or functions (brief §3.1). */
const GRAPH_WORKFLOW_PATH_PATTERN = /^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$/;

function messageKey(field: string, error: string): string {
	return `pages.graphWorkflows.form.${field}.${error}`;
}

function parsesTo(kind: "object" | "json", value: string | null | undefined): boolean {
	const trimmed = (value ?? "").trim();
	if (trimmed.length === 0) {
		return true;
	}
	try {
		const parsed: unknown = JSON.parse(trimmed);
		return kind === "json" || (typeof parsed === "object" && parsed !== null && !Array.isArray(parsed));
	} catch {
		return false;
	}
}

/** JSON text that must parse to an OBJECT when it is not empty. Empty means "unset", which every such field allows. */
function jsonObjectText(field: string) {
	return z
		.string()
		.nullable()
		.refine((value) => parsesTo("object", value), { message: messageKey(field, "notObject") });
}

/** A dot path, optional. An empty string is "unset"; the structural rules decide whether unset is legal here. */
function optionalDotPath(field: string) {
	return z
		.string()
		.nullable()
		.refine((value) => (value ?? "").trim().length === 0 || GRAPH_WORKFLOW_PATH_PATTERN.test(value ?? ""), {
			message: messageKey(field, "invalid"),
		});
}

/** The fields every node carries, whatever its kind. `maxAttempts` mirrors the server's per-node retry budget. */
export const nodeCommonSchema = z.object({
	key: z.string().regex(GRAPH_WORKFLOW_KEY_PATTERN, { message: messageKey("key", "invalid") }),
	label: z.string(),
	maxAttempts: z
		.number()
		.int({ message: messageKey("maxAttempts", "range") })
		.min(1, { message: messageKey("maxAttempts", "range") })
		.max(10, { message: messageKey("maxAttempts", "range") })
		.optional(),
	timeoutSeconds: z
		.number()
		.int({ message: messageKey("timeoutSeconds", "min") })
		.min(1, { message: messageKey("timeoutSeconds", "min") })
		.nullable()
		.optional(),
});

export const startConfigSchema = z.object({
	inputSchema: jsonObjectText("inputSchema"),
	defaultInput: z
		.string()
		.nullable()
		.refine((value) => parsesTo("json", value), { message: messageKey("defaultInput", "invalidJson") }),
});

export const agentConfigSchema = z.object({
	agentDefinitionId: z.string().nullable(),
	instructions: z
		.string()
		.trim()
		.min(1, { message: messageKey("instructions", "required") }),
	model: z.string().nullable(),
	reasoningEffort: z.string().nullable(),
	responseJsonSchema: jsonObjectText("responseJsonSchema"),
	includeUpstreamOutputs: z.boolean(),
});

export const toolConfigSchema = z.object({
	toolName: z
		.string()
		.nullable()
		.refine((value) => (value ?? "").trim().length > 0, { message: messageKey("toolName", "required") }),
	argumentsJson: jsonObjectText("argumentsJson"),
	argumentBindings: z.array(
		z.object({
			parameter: z.string(),
			path: z
				.string()
				.trim()
				.min(1, { message: messageKey("argumentBindings", "pathRequired") }),
		}),
	),
});

export const conditionConfigSchema = z.object({ path: optionalDotPath("path") });

export const pauseConfigSchema = z.object({
	prompt: z
		.string()
		.trim()
		.min(1, { message: messageKey("prompt", "required") }),
	allowedDecisions: z.array(z.string()).min(1, { message: messageKey("allowedDecisions", "required") }),
	requireComment: z.boolean(),
});

export const endConfigSchema = z.object({
	outcome: z
		.string()
		.trim()
		.min(1, { message: messageKey("outcome", "required") }),
	resultPath: optionalDotPath("resultPath"),
});

/** The edge drawer's path / op / value trio. `Exists` and `NotExists` take no value, so the field is hidden there. */
export const edgeConditionSchema = z
	.object({
		path: optionalDotPath("path").optional(),
		op: z.enum(graphWorkflowConditionOperators),
		value: z.string(),
	})
	.refine((condition) => condition.op === "Exists" || condition.op === "NotExists" || condition.value.length > 0, {
		message: messageKey("condition", "valueRequired"),
		path: ["value"],
	});
