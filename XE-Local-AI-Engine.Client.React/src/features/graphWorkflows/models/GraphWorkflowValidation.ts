// The client half of the graph editor's save gate.
//
// `validateGraphWorkflowGraph` mirrors a deliberate SUBSET of the server's graph parser (brief §3.1) — the rules the
// canvas can actually break — so the operator is told which node is wrong instead of reading a 400 that names none.
// THE SERVER WINS on any disagreement: this list is advisory, a save still renders whatever the server refuses, and
// `serverErrorsToIssues` maps the server's own `errors[]` into the same shape so one component draws both.
//
// Deliberately NOT mirrored, because the client cannot answer them honestly:
//   - whether a Tool node's `toolName` resolves to a `ReadLocal`, no-approval tool. `GET graph-workflows/tools`
//     is already filtered server-side; re-deriving eligibility in TypeScript would be a second, drifting rule.
//   - whether an Agent node's effective model is a cloud model (refused at run start) — that is a
//     resolution over agent definitions and installed runtimes, not over the graph.
//   - `responseJsonSchema` being a valid JSON *schema*. "Parses as an object" is checked; the rest is the server's.
//
// Two mechanisms, deliberately: the structural rules above are pure functions over the WIRE graph and gate Save, while
// the Zod schemas in GraphWorkflowConfigSchemas run per field in the drawer and only produce a field message. Their messages are full i18n
// KEYS, so a form renders `t(issue.message)` without a lookup table of its own.

import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	GRAPH_WORKFLOW_MAX_NODES,
	type GraphWorkflowGraph,
	type GraphWorkflowGraphEdge,
	type GraphWorkflowGraphNode,
	type GraphWorkflowNodeKind,
	type GraphWorkflowValidationErrorResponse,
	graphWorkflowDecisionProviders,
	graphWorkflowNodeKinds,
	narrowGraphWorkflowDefinitionKind,
	narrowGraphWorkflowJoinPolicy,
	normalizeGraphWorkflowConditionOperator,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

/**
 * One rule, one i18n key: the member name IS the suffix under `pages.graphWorkflows.definition.issues`, so a new rule
 * cannot ship without a message (`I18nParity.test.ts` asserts the whole array).
 *
 * `serverRejected` and `serverWarned` are the two members no client check produces — they carry the server's own
 * sentence through the same shape, so the validation strip has one render path rather than three. Their i18n messages
 * are only ever the fallback for a server that sent an empty string.
 */
export const graphWorkflowGraphRules = [
	"duplicateNodeKey",
	"missingNodeKey",
	"invalidNodeKey",
	"unknownEdgeEndpoint",
	"duplicateEdgeKey",
	"missingEdgeKey",
	"invalidEdgeKey",
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
	"agentInstructionsMissing",
	"llmPromptMissing",
	"invalidInputBindings",
	"pausePromptMissing",
	"pauseNoDecisions",
	"endOutcomeMissing",
	"chatSettingsOnStandardGraph",
	"chatInputOutsideChatGraph",
	"chatInputPromptMissing",
	"chatInputNeedsUnconditionalEdge",
	"publishToChatOutsideChatGraph",
	"includeAttachmentsNotAccepted",
	"decisionQuestionMissing",
	"decisionLabelsInvalid",
	"decisionProviderUnknown",
	"decisionTypeUnsupportedByProvider",
	"decisionScoreLevelsInvalid",
	"decisionNoulHasLabels",
	"decisionCriteriaKeyUnknown",
	"decisionCriteriaTooLong",
	"decisionModelRequired",
	"decisionInputBindingRequired",
	"pauseOffersAnswer",
	"serverRejected",
	"serverWarned",
] as const;
export type GraphWorkflowGraphRule = (typeof graphWorkflowGraphRules)[number];

export interface GraphWorkflowGraphIssue {
	readonly rule: GraphWorkflowGraphRule;
	/** The node or edge key the rule is about — one namespace, so it is never ambiguous which it points at. */
	readonly subject?: string;
	/** The server's own text, for `serverRejected` and `serverWarned`. Client rules carry none: their message is the i18n key. */
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
 * True when the graph has a cycle. Iterative depth-first with a color map, because a graph authored by hand can be
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
		case "LlmCall":
			return ["responseJsonSchema"];
		case "Tool":
			return ["arguments"];
		default:
			return [];
	}
}

/** The graph-level facts the chat rules read: is this a Chat graph, and does it accept attachments. */
interface GraphWorkflowChatContext {
	readonly isChat: boolean;
	readonly acceptsAttachments: boolean;
}

/** `GraphWorkflowGraph.ParseLabels`: 2..`max` distinct, non-blank strings of at most 64 characters (32 labels, 10 levels). */
export function labelsAreValid(value: unknown, max = 32): boolean {
	if (!Array.isArray(value) || value.length < 2 || value.length > max) {
		return false;
	}
	const seen = new Set<string>();
	for (const label of value) {
		if (typeof label !== "string" || label.trim().length === 0 || label.length > 64 || seen.has(label)) {
			return false;
		}
		seen.add(label);
	}
	return true;
}

/** The server's cap on one `criteria` description, refused at save. */
export const GRAPH_WORKFLOW_MAX_CRITERION_LENGTH = 512;

function isPresent(value: unknown): boolean {
	return value !== undefined && value !== null;
}

/**
 * The DecisionModel type × provider matrix, as the server's parser applies it: `choice` takes 2..32 labels and
 * criteria keyed by them; `score` takes 2..10 ordered levels and no criteria; `noul` takes neither; `llm` answers
 * `choice` only; `systemone` must name its model. An unknown `questionType` is left to the server's message.
 */
function decisionShapeIssues(key: string, config: Record<string, unknown>): readonly GraphWorkflowGraphIssue[] {
	const issues: GraphWorkflowGraphIssue[] = [];
	const type = text(config["questionType"]).trim() || "choice";
	const labels = config["labels"];
	const criteria = config["criteria"];
	if (type === "choice") {
		if (!labelsAreValid(labels)) {
			issues.push({ rule: "decisionLabelsInvalid", subject: key });
		}
		if (isPresent(criteria)) {
			const known = Array.isArray(labels) ? labels : [];
			if (!isPlainObject(criteria) || Object.keys(criteria as object).some((label) => !known.includes(label))) {
				issues.push({ rule: "decisionCriteriaKeyUnknown", subject: key });
			}
			if (
				isPlainObject(criteria) &&
				Object.values(criteria as object).some(
					(description) => typeof description === "string" && description.length > GRAPH_WORKFLOW_MAX_CRITERION_LENGTH,
				)
			) {
				issues.push({ rule: "decisionCriteriaTooLong", subject: key });
			}
		}
	} else if (type === "score") {
		if (!labelsAreValid(labels, 10) || isPresent(criteria)) {
			issues.push({ rule: "decisionScoreLevelsInvalid", subject: key });
		}
	} else if (type === "noul" && (isPresent(labels) || isPresent(criteria))) {
		issues.push({ rule: "decisionNoulHasLabels", subject: key });
	}
	const provider = text(config["provider"]).trim() || "llm";
	if (!(graphWorkflowDecisionProviders as readonly string[]).includes(provider)) {
		issues.push({ rule: "decisionProviderUnknown", subject: key });
	}
	// An unknown type is the server's to name; "yes/no or score" would be the wrong sentence for it.
	if (provider === "llm" && (type === "noul" || type === "score")) {
		issues.push({ rule: "decisionTypeUnsupportedByProvider", subject: key });
	}
	if (provider === "systemone" && text(config["model"]).trim().length === 0) {
		issues.push({ rule: "decisionModelRequired", subject: key });
	}
	// systemone decides on the bound text alone (the question is its instructions), so with no binding it has nothing
	// to read. `llm` still has the question as its prompt and stays unaffected.
	const bindings = config["inputBindings"];
	if (provider === "systemone" && !(isPlainObject(bindings) && Object.keys(bindings as object).length > 0)) {
		issues.push({ rule: "decisionInputBindingRequired", subject: key });
	}
	return issues;
}

/** The chat-contract refusals that live on one node's config (backend report "For the frontend worker"). */
function chatNodeIssues(
	kind: GraphWorkflowNodeKind,
	key: string,
	config: Record<string, unknown>,
	chat: GraphWorkflowChatContext,
): readonly GraphWorkflowGraphIssue[] {
	const issues: GraphWorkflowGraphIssue[] = [];
	if ((kind === "Agent" || kind === "LlmCall" || kind === "End") && config["publishToChat"] === true && !chat.isChat) {
		issues.push({ rule: "publishToChatOutsideChatGraph", subject: key });
	}
	if ((kind === "Agent" || kind === "LlmCall") && config["includeAttachments"] === true && !chat.acceptsAttachments) {
		issues.push({ rule: "includeAttachmentsNotAccepted", subject: key });
	}
	if (kind === "ChatInput") {
		if (!chat.isChat) {
			issues.push({ rule: "chatInputOutsideChatGraph", subject: key });
		}
		if (text(config["prompt"]).trim().length === 0) {
			issues.push({ rule: "chatInputPromptMissing", subject: key });
		}
	}
	if (kind === "DecisionModel") {
		if (text(config["question"]).trim().length === 0) {
			issues.push({ rule: "decisionQuestionMissing", subject: key });
		}
		issues.push(...decisionShapeIssues(key, config));
	}
	if (kind === "Pause") {
		const allowed = config["allowedDecisions"];
		if (Array.isArray(allowed) && allowed.includes("Answer")) {
			issues.push({ rule: "pauseOffersAnswer", subject: key });
		}
	}
	return issues;
}

/** Node-level rules: keys, kinds, and the config members whose SHAPE the server enforces. */
function nodeIssues(
	nodes: readonly GraphWorkflowGraphNode[],
	chat: GraphWorkflowChatContext,
): readonly GraphWorkflowGraphIssue[] {
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
			// A closed vocabulary: the server refuses an unknown kind, and the canvas has no card to draw for it.
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
		// The four members the server's `ParseConfig` refuses outright. Mirrored because the drawer authors all of them
		// and a save that fails on an empty prompt should say which node, not just 400.
		if (kind === "Tool" && text(config["toolName"]).trim().length === 0) {
			issues.push({ rule: "toolNameMissing", subject: key });
		}
		if (kind === "Agent" && text(config["instructions"]).trim().length === 0) {
			issues.push({ rule: "agentInstructionsMissing", subject: key });
		}
		if (kind === "LlmCall" && text(config["prompt"]).trim().length === 0) {
			issues.push({ rule: "llmPromptMissing", subject: key });
		}
		if (kind === "Pause") {
			if (text(config["prompt"]).trim().length === 0) {
				issues.push({ rule: "pausePromptMissing", subject: key });
			}
			const allowed = config["allowedDecisions"];
			if (!Array.isArray(allowed) || allowed.length === 0) {
				issues.push({ rule: "pauseNoDecisions", subject: key });
			}
		}
		if (kind === "End" && text(config["outcome"]).trim().length === 0) {
			issues.push({ rule: "endOutcomeMissing", subject: key });
		}
		issues.push(...chatNodeIssues(kind, key, config, chat));
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
		// Edge keys answer to the same charset and the same namespace as node keys (brief §3.1), which is what keeps an
		// issue's `subject` unambiguous about what it points at.
		const key = edge.key ?? "";
		if (key.length === 0) {
			issues.push({ rule: "missingEdgeKey", subject: edgeSubject(edge) });
		} else {
			if (!GRAPH_WORKFLOW_KEY_PATTERN.test(key)) {
				issues.push({ rule: "invalidEdgeKey", subject: key });
			}
			if (edgeKeys.has(key) || nodeByKey.has(key)) {
				issues.push({ rule: "duplicateEdgeKey", subject: key });
			}
			edgeKeys.add(key);
		}

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
			// An edge with no path of its own inherits its source Condition node's `config.path`; every
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
		// Zero out-edges is `danglingNonEnd` already; this is the ChatInput whose every way out is conditional, so an
		// answer no condition accepts would strand the run.
		if (kind === "ChatInput" && out.length > 0 && out.every((edge) => hasCondition(edge))) {
			issues.push({ rule: "chatInputNeedsUnconditionalEdge", subject: key });
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
	const isChat = narrowGraphWorkflowDefinitionKind(graph.kind) === "Chat";
	const chatBlock = graph.chat;
	const hasChatBlock = chatBlock !== undefined && chatBlock !== null;
	if (hasChatBlock && !isChat) {
		issues.push({ rule: "chatSettingsOnStandardGraph" });
	}
	issues.push(
		...nodeIssues(nodes, {
			isChat,
			acceptsAttachments: isChat && hasChatBlock && configRecord(chatBlock)["acceptsAttachments"] === true,
		}),
	);

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
 * The issues a LOADED wire graph carries that the canvas cannot round-trip: an operator token this client cannot read
 * (the condition is dropped) and a node kind it cannot draw (narrowed to `End`). Both are lost the moment the graph
 * becomes canvas state, so the loader runs this over the wire document it just read and holds the result until a
 * different graph is loaded — that is what stops a save from silently rewriting the branch or the node.
 */
export function loadedGraphIssues(graph: GraphWorkflowGraph | undefined): readonly GraphWorkflowGraphIssue[] {
	return validateGraphWorkflowGraph(graph).filter(
		(issue) => issue.rule === "unknownConditionOperator" || issue.rule === "unknownNodeKind",
	);
}

/**
 * The server's `errors[]` in the client's shape (hybrid: keyed failures accumulate, a malformed document
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

/**
 * The server's `warnings[]` — the same `(key, message)` shape as its errors, and keyed on the node the warning is
 * ABOUT, so a warning selects its subject exactly as an error does. Non-blocking by construction server-side
 * (`GraphWorkflowGraph.Warnings` never reaches `GraphWorkflowValidationException`) and non-blocking here because the
 * page holds them in their own list, which the save gate never reads.
 */
export function serverWarningsToIssues(
	warnings: readonly GraphWorkflowValidationErrorResponse[] | undefined,
): readonly GraphWorkflowGraphIssue[] {
	return (warnings ?? []).map((warning) => ({
		rule: "serverWarned" as const,
		subject: warning.key ?? undefined,
		message: warning.message,
	}));
}
