// Wire ↔ canvas mapping for the graph editor. Pure: no React, and only React Flow's `Node`/`Edge` TYPES.
//
// Preview's `PreviewCanvasNodeData` is a flat interface with optional agent fields; with eight kinds and four of them
// carrying real config that becomes an untyped grab-bag, so the node data here is a DISCRIMINATED UNION on `kind`.
//
// Three JSON-shaped fields (`defaultInput`, `responseJsonSchema`, `argumentsJson`) and every edge condition `value` are
// held on the canvas as STRINGS, not parsed objects. A half-typed JSON object is not representable as an object, and
// re-serialising on every keystroke destroys the operator's formatting. They are parsed once, at `canvasToGraph` time,
// and a parse failure becomes a `GraphWorkflowGraphIssue` rather than a thrown render.
//
// `React Flow node id === node key` and `edge.id === edge.key`. One identifier addresses the card, the edge endpoint,
// the node run and the decision, so "which node did the run fail on" needs no lookup table. Node and edge keys share
// ONE namespace (brief §3.1), which is what keeps a validation issue's `subject` unambiguous about what it points at.

import type { Edge, Node } from "@xyflow/react";

import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	type GraphWorkflowConditionOperator,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowFailureClass,
	type GraphWorkflowGraph,
	type GraphWorkflowGraphEdge,
	type GraphWorkflowGraphNode,
	type GraphWorkflowJoinPolicy,
	type GraphWorkflowNodeKind,
	type GraphWorkflowNodeRunStatus,
	graphWorkflowDecisionKinds,
	graphWorkflowDefaultMaxAttempts,
	narrowGraphWorkflowJoinPolicy,
	narrowGraphWorkflowNodeKind,
	normalizeGraphWorkflowConditionOperator,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

/** What a run adds to a card. Absent on the editor's canvas, where nothing has run. */
export interface GraphWorkflowCanvasRunState {
	readonly status: GraphWorkflowNodeRunStatus;
	readonly attempt: number;
	readonly failureClass: GraphWorkflowFailureClass;
	readonly pendingDecisionKind?: GraphWorkflowDecisionKind;
}

export interface GraphWorkflowNodeBase extends Record<string, unknown> {
	/** The author-stable node key. Also the React Flow node id — see the header. */
	readonly key: string;
	readonly label: string;
	/** Brief §3.1: a property of EVERY node, not just `Join`. `All` is the parser's default. */
	readonly joinPolicy: GraphWorkflowJoinPolicy;
	readonly maxAttempts?: number;
	readonly timeoutSeconds?: number;
	readonly runState?: GraphWorkflowCanvasRunState;
}

/** A Mantine list of rows needs stable order; the wire wants an object map. `canvasToGraph` converts. */
export interface GraphWorkflowArgumentBinding {
	readonly parameter: string;
	readonly path: string;
}

export type GraphWorkflowCanvasNodeData =
	| (GraphWorkflowNodeBase & {
			readonly kind: "Start";
			readonly inputSchema: string | null;
			readonly defaultInput: string | null;
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "Agent";
			readonly agentDefinitionId: string | null;
			readonly instructions: string;
			readonly model: string | null;
			readonly reasoningEffort: string | null;
			readonly responseJsonSchema: string | null;
			readonly includeUpstreamOutputs: boolean;
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "Tool";
			readonly toolName: string | null;
			readonly argumentsJson: string;
			readonly argumentBindings: readonly GraphWorkflowArgumentBinding[];
	  })
	| (GraphWorkflowNodeBase & { readonly kind: "Condition"; readonly path: string | null })
	| (GraphWorkflowNodeBase & { readonly kind: "Parallel" | "Join" })
	| (GraphWorkflowNodeBase & {
			readonly kind: "Pause";
			readonly prompt: string;
			readonly allowedDecisions: readonly GraphWorkflowDecisionKind[];
			readonly requireComment: boolean;
	  })
	| (GraphWorkflowNodeBase & { readonly kind: "End"; readonly outcome: string; readonly resultPath: string | null });

export interface GraphWorkflowCanvasEdgeCondition {
	readonly path?: string;
	readonly op: GraphWorkflowConditionOperator;
	/** A STRING on the canvas, for the same reason the JSON fields are: it is parsed once, at `canvasToGraph`. */
	readonly value: string;
}

export interface GraphWorkflowEdgeData extends Record<string, unknown> {
	readonly label?: string;
	readonly condition?: GraphWorkflowCanvasEdgeCondition;
}

export type GraphWorkflowCanvasNode = Node<GraphWorkflowCanvasNodeData>;
export type GraphWorkflowCanvasEdge = Edge<GraphWorkflowEdgeData>;

export interface GraphWorkflowCanvas {
	readonly nodes: GraphWorkflowCanvasNode[];
	readonly edges: GraphWorkflowCanvasEdge[];
}

/** React Flow type keys — the keys the canvas registers in its `nodeTypes` map, one component per kind. */
export const graphWorkflowNodeTypeByKind: Record<GraphWorkflowNodeKind, string> = {
	Start: "start",
	Agent: "agent",
	Tool: "tool",
	Condition: "condition",
	Parallel: "parallel",
	Join: "join",
	Pause: "pause",
	End: "end",
};

// ---------------------------------------------------------------------------------------------------------------
// Reading the wire
// ---------------------------------------------------------------------------------------------------------------

function configRecord(config: unknown): Record<string, unknown> {
	return typeof config === "object" && config !== null && !Array.isArray(config) ? (config as Record<string, unknown>) : {};
}

function stringOrNull(value: unknown): string | null {
	return typeof value === "string" ? value : null;
}

function stringOrEmpty(value: unknown): string {
	return typeof value === "string" ? value : "";
}

function booleanOr(value: unknown, fallback: boolean): boolean {
	return typeof value === "boolean" ? value : fallback;
}

function numberOrUndefined(value: unknown): number | undefined {
	return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/**
 * A JSON-shaped wire member as editable text. An object or array is pretty-printed; a STRING is kept verbatim, because
 * a graph saved by an older client may hold half-typed text there and re-quoting it would destroy what the operator
 * wrote. The cost is deliberate and visible: text that is not JSON comes back as an `invalidJson` issue on the next
 * save rather than silently surviving another round trip.
 */
function jsonText(value: unknown): string | null {
	if (value === undefined || value === null) {
		return null;
	}
	if (typeof value === "string") {
		return value;
	}
	return JSON.stringify(value, null, 2) ?? null;
}

function bindingsFromWire(value: unknown): readonly GraphWorkflowArgumentBinding[] {
	return Object.entries(configRecord(value)).flatMap(([parameter, path]) =>
		typeof path === "string" ? [{ parameter, path }] : [],
	);
}

function nodeDataFromWire(node: GraphWorkflowGraphNode): GraphWorkflowCanvasNodeData {
	const config = configRecord(node.config);
	const base = {
		key: node.key ?? "",
		label: node.label ?? "",
		joinPolicy: narrowGraphWorkflowJoinPolicy(node.joinPolicy),
		maxAttempts: numberOrUndefined(node.maxAttempts),
		timeoutSeconds: numberOrUndefined(node.timeoutSeconds),
	};
	// A malformed or missing member reads as the kind's default rather than throwing: `config` is `unknown` on the wire
	// and the editor has to open whatever the server stored.
	switch (narrowGraphWorkflowNodeKind(node.kind)) {
		case "Start":
			return {
				...base,
				kind: "Start",
				inputSchema: jsonText(config["inputSchema"]),
				defaultInput: jsonText(config["defaultInput"]),
			};
		case "Agent":
			return {
				...base,
				kind: "Agent",
				agentDefinitionId: stringOrNull(config["agentDefinitionId"]),
				instructions: stringOrEmpty(config["instructions"]),
				model: stringOrNull(config["model"]),
				reasoningEffort: stringOrNull(config["reasoningEffort"]),
				responseJsonSchema: jsonText(config["responseJsonSchema"]),
				includeUpstreamOutputs: booleanOr(config["includeUpstreamOutputs"], true),
			};
		case "Tool":
			return {
				...base,
				kind: "Tool",
				toolName: stringOrNull(config["toolName"]),
				argumentsJson: jsonText(config["arguments"]) ?? "",
				argumentBindings: bindingsFromWire(config["argumentBindings"]),
			};
		case "Condition":
			return { ...base, kind: "Condition", path: stringOrNull(config["path"]) };
		case "Parallel":
			return { ...base, kind: "Parallel" };
		case "Join":
			return { ...base, kind: "Join" };
		case "Pause":
			return {
				...base,
				kind: "Pause",
				prompt: stringOrEmpty(config["prompt"]),
				allowedDecisions: toGraphWorkflowDecisionKinds(
					Array.isArray(config["allowedDecisions"])
						? config["allowedDecisions"].filter((entry): entry is string => typeof entry === "string")
						: [],
				),
				requireComment: booleanOr(config["requireComment"], false),
			};
		// `default` IS the End case: `narrowGraphWorkflowNodeKind` answers one of the eight members and the seven above
		// are handled, so TypeScript narrows `node` here exactly as a `case "End"` would.
		default:
			return {
				...base,
				kind: "End",
				outcome: stringOrEmpty(config["outcome"]),
				resultPath: stringOrNull(config["resultPath"]),
			};
	}
}

/** A wire condition `value` as canvas text: a string stays itself, so `"Approve"` reads as `Approve`, not `"Approve"`. */
function conditionValueText(value: unknown): string {
	if (typeof value === "string") {
		return value;
	}
	return value === undefined ? "" : (JSON.stringify(value) ?? "");
}

function conditionFromWire(edge: GraphWorkflowGraphEdge): GraphWorkflowCanvasEdgeCondition | undefined {
	const condition = edge.condition;
	if (condition === undefined || condition === null) {
		return undefined;
	}
	const op = normalizeGraphWorkflowConditionOperator(condition.op);
	if (op === undefined) {
		// An absent or unknown `op` DROPS the condition on the canvas — the edge stays, unconditional — rather than
		// guessing `Eq` and silently rewriting the branch on the next save. The operator is told through
		// `validateGraphWorkflowGraph`'s `unknownConditionOperator` issue over the loaded WIRE graph, where the token is
		// still readable. The server's parser is the authority on what the token meant.
		return undefined;
	}
	const path = stringOrEmpty(condition.path);
	return { ...(path.length > 0 ? { path } : {}), op, value: conditionValueText(condition.value) };
}

/**
 * Which source handle this edge left from. `sourceHandle` is authoring metadata the runtime ignores, so an older graph
 * (or S4's importer) carries none — belt and braces, it is re-derived from the label, then from the condition value,
 * and only then falls back to the default handle.
 */
function sourceHandleFor(
	edge: GraphWorkflowGraphEdge,
	sourceKind: GraphWorkflowNodeKind | undefined,
	condition: GraphWorkflowCanvasEdgeCondition | undefined,
): string | undefined {
	const stored = stringOrEmpty(edge.sourceHandle);
	if (stored.length > 0) {
		return stored;
	}
	const label = stringOrEmpty(edge.label);
	if (sourceKind === "Condition") {
		if (label === "true" || label === "false") {
			return label;
		}
		return condition?.op === "Eq" && (condition.value === "true" || condition.value === "false") ? condition.value : undefined;
	}
	if (sourceKind === "Pause") {
		const decisions: readonly string[] = graphWorkflowDecisionKinds;
		if (decisions.includes(label)) {
			return label;
		}
		return condition !== undefined && decisions.includes(condition.value) ? condition.value : undefined;
	}
	return undefined;
}

/**
 * The wire graph as React Flow nodes and edges. A node WITHOUT a position is laid out (ruling C4: `position` is
 * optional, and a laid-out node is dirty by construction, so the first save persists what the layout computed); a node
 * with one keeps it verbatim. `relayout` is the "Auto-arrange" path: every position recomputed, nothing kept.
 */
export function graphToCanvas(
	graph: GraphWorkflowGraph | undefined,
	options?: { readonly relayout?: boolean },
): GraphWorkflowCanvas {
	const wireNodes = graph?.nodes ?? [];
	const wireEdges = graph?.edges ?? [];
	const kindByKey = new Map<string, GraphWorkflowNodeKind>(
		wireNodes.map((node) => [node.key ?? "", narrowGraphWorkflowNodeKind(node.kind)]),
	);
	const layout = layoutGraphWorkflow(
		wireNodes.map((node) => ({ key: node.key ?? "" })),
		wireEdges.map((edge) => ({ from: edge.from ?? "", to: edge.to ?? "" })),
	);

	const nodes = wireNodes.map((node): GraphWorkflowCanvasNode => {
		const data = nodeDataFromWire(node);
		const stored = node.position;
		const placed = layout.positions.get(data.key);
		const position =
			stored && options?.relayout !== true ? { x: stored.x ?? 0, y: stored.y ?? 0 } : { x: placed?.x ?? 0, y: placed?.y ?? 0 };
		return { id: data.key, type: graphWorkflowNodeTypeByKind[data.kind], position, data };
	});

	const edges = wireEdges.map((edge): GraphWorkflowCanvasEdge => {
		const condition = conditionFromWire(edge);
		const label = stringOrEmpty(edge.label);
		const handle = sourceHandleFor(edge, kindByKey.get(edge.from ?? ""), condition);
		return {
			id: edge.key ?? "",
			source: edge.from ?? "",
			target: edge.to ?? "",
			sourceHandle: handle,
			// React Flow renders the top-level `label` natively, so a branch is readable on the canvas without opening a
			// panel; `data.label` is the value the drawer edits. Both are written, and `canvasToGraph` prefers `data`.
			label: label.length > 0 ? label : undefined,
			data: { ...(label.length > 0 ? { label } : {}), ...(condition ? { condition } : {}) },
		};
	});

	return { nodes, edges };
}

// ---------------------------------------------------------------------------------------------------------------
// Writing the wire
// ---------------------------------------------------------------------------------------------------------------

/** JSON text → a wire value. Empty is "unset" (`null`); unparseable is an `invalidJson` issue against its node. */
function parseJsonField(text: string | null, key: string, issues: GraphWorkflowGraphIssue[], mustBeObject: boolean): unknown {
	const trimmed = (text ?? "").trim();
	if (trimmed.length === 0) {
		return null;
	}
	try {
		const parsed: unknown = JSON.parse(trimmed);
		if (mustBeObject && (typeof parsed !== "object" || parsed === null || Array.isArray(parsed))) {
			issues.push({ rule: "invalidJson", subject: key });
			return null;
		}
		return parsed;
	} catch {
		issues.push({ rule: "invalidJson", subject: key });
		return null;
	}
}

/**
 * `argumentsJson` → the wire's `arguments` object. Unparseable text still emits `{}` rather than `null`, unlike the
 * other JSON fields: `arguments` is a required object server-side, so `null` would trade a precise client issue for a
 * server 400 about the wrong thing. The `invalidJson` issue is raised either way.
 */
function parseArguments(text: string, key: string, issues: GraphWorkflowGraphIssue[]): Record<string, unknown> {
	const parsed = parseJsonField(text, key, issues, true);
	return configRecord(parsed);
}

function bindingsToWire(bindings: readonly GraphWorkflowArgumentBinding[]): Record<string, string> | undefined {
	// `Object.fromEntries` is last-wins on a duplicate parameter, which is what the wire map can express; an empty
	// parameter names nothing and is dropped. An empty map is omitted so a stored graph stays as small as it was.
	const map = Object.fromEntries(
		bindings.filter((binding) => binding.parameter.length > 0).map((binding) => [binding.parameter, binding.path]),
	);
	return Object.keys(map).length > 0 ? map : undefined;
}

function configToWire(data: GraphWorkflowCanvasNodeData, issues: GraphWorkflowGraphIssue[]): unknown {
	switch (data.kind) {
		case "Start":
			return {
				inputSchema: parseJsonField(data.inputSchema, data.key, issues, true),
				defaultInput: parseJsonField(data.defaultInput, data.key, issues, false),
			};
		case "Agent":
			return {
				agentDefinitionId: data.agentDefinitionId,
				instructions: data.instructions,
				model: data.model,
				reasoningEffort: data.reasoningEffort,
				responseJsonSchema: parseJsonField(data.responseJsonSchema, data.key, issues, true),
				includeUpstreamOutputs: data.includeUpstreamOutputs,
			};
		case "Tool": {
			const bindings = bindingsToWire(data.argumentBindings);
			return {
				toolName: data.toolName,
				arguments: parseArguments(data.argumentsJson, data.key, issues),
				...(bindings ? { argumentBindings: bindings } : {}),
			};
		}
		case "Condition":
			return { path: data.path };
		case "Parallel":
		case "Join":
			return {};
		case "Pause":
			return {
				prompt: data.prompt,
				allowedDecisions: data.allowedDecisions,
				requireComment: data.requireComment,
			};
		// `default` IS the End case — see `nodeDataFromWire`.
		default:
			return { outcome: data.outcome, resultPath: data.resultPath };
	}
}

/** A canvas condition value back to JSON, falling back to the raw string — so `Approve` stays a string, `true` a boolean. */
function conditionValueToWire(value: string): unknown {
	try {
		return JSON.parse(value);
	} catch {
		return value;
	}
}

export interface GraphWorkflowCanvasConversion {
	readonly graph: GraphWorkflowGraph;
	readonly issues: readonly GraphWorkflowGraphIssue[];
}

/**
 * The canvas back to the wire graph. NEVER throws: a JSON text field that does not parse yields an `invalidJson` issue
 * keyed to its node and the member is emitted as `null`, so the operator is told which card is wrong instead of losing
 * the canvas to an exception.
 */
export function canvasToGraph(
	nodes: readonly GraphWorkflowCanvasNode[],
	edges: readonly GraphWorkflowCanvasEdge[],
): GraphWorkflowCanvasConversion {
	const issues: GraphWorkflowGraphIssue[] = [];
	const graphNodes: GraphWorkflowGraphNode[] = nodes.map((node) => {
		const data = node.data;
		return {
			key: data.key,
			kind: data.kind,
			...(data.label.length > 0 ? { label: data.label } : {}),
			// Positions are always written and always integers: a sub-pixel drag would otherwise make every reopen dirty.
			position: { x: Math.round(node.position.x), y: Math.round(node.position.y) },
			...(data.maxAttempts === undefined ? {} : { maxAttempts: data.maxAttempts }),
			...(data.timeoutSeconds === undefined ? {} : { timeoutSeconds: data.timeoutSeconds }),
			// Only `Any` is written: `All` is the parser's default, so emitting it would grow every stored graph and make
			// a graph authored here differ from the same graph authored through the API. `graphWorkflowsEqual` reads
			// absent and `All` as the same thing.
			...(data.joinPolicy === "Any" ? { joinPolicy: "Any" } : {}),
			config: configToWire(data, issues),
		};
	});

	const graphEdges: GraphWorkflowGraphEdge[] = edges.map((edge) => {
		const condition = edge.data?.condition;
		const label = edge.data?.label ?? (typeof edge.label === "string" ? edge.label : "");
		const handle = edge.sourceHandle ?? "";
		const isExistence = condition?.op === "Exists" || condition?.op === "NotExists";
		return {
			key: edge.id,
			from: edge.source,
			to: edge.target,
			...(label.length > 0 ? { label } : {}),
			...(handle.length > 0 ? { sourceHandle: handle } : {}),
			...(condition
				? {
						condition: {
							...(condition.path && condition.path.length > 0 ? { path: condition.path } : {}),
							op: condition.op,
							// `Exists`/`NotExists` take no operand; sending one would be a value the server never reads.
							...(isExistence ? {} : { value: conditionValueToWire(condition.value) }),
						},
					}
				: {}),
		};
	});

	return { graph: { schemaVersion: 1, nodes: graphNodes, edges: graphEdges }, issues };
}

// ---------------------------------------------------------------------------------------------------------------
// Authoring helpers
// ---------------------------------------------------------------------------------------------------------------

/** A fresh node's data, with the defaults the plan fixes — including F-1's `maxAttempts` (3 for Agent and Tool, else 1). */
export function defaultNodeData(kind: GraphWorkflowNodeKind, key: string): GraphWorkflowCanvasNodeData {
	const base = { key, label: "", joinPolicy: "All" as const, maxAttempts: graphWorkflowDefaultMaxAttempts(kind) };
	switch (kind) {
		case "Start":
			return { ...base, kind, inputSchema: null, defaultInput: null };
		case "Agent":
			return {
				...base,
				kind,
				agentDefinitionId: null,
				instructions: "",
				model: null,
				reasoningEffort: null,
				responseJsonSchema: null,
				includeUpstreamOutputs: true,
			};
		case "Tool":
			return { ...base, kind, toolName: null, argumentsJson: "", argumentBindings: [] };
		case "Condition":
			return { ...base, kind, path: null };
		case "Parallel":
		case "Join":
			return { ...base, kind };
		case "Pause":
			return { ...base, kind, prompt: "", allowedDecisions: ["Approve", "Reject"], requireComment: false };
		// `default` IS the End case — see `nodeDataFromWire`.
		default:
			return { ...base, kind, outcome: "completed", resultPath: null };
	}
}

function mintKey(prefix: string, existingKeys: Iterable<string>): string {
	const taken = new Set(existingKeys);
	for (let index = 1; ; index += 1) {
		const candidate = `${prefix}${index}`;
		if (!taken.has(candidate)) {
			return candidate;
		}
	}
}

/** `agent-1`, `agent-2`, … — the kind slug plus the lowest free integer, over the ONE key namespace. */
export function mintNodeKey(kind: GraphWorkflowNodeKind, existingKeys: Iterable<string>): string {
	return mintKey(`${graphWorkflowNodeTypeByKind[kind]}-`, existingKeys);
}

/** `e1`, `e2`, … — Preview's `${source}->${target}` scheme cannot be used: a Pause routing both decisions to one End
 * is the natural authoring shape and that scheme collides on it. */
export function mintEdgeKey(existingKeys: Iterable<string>): string {
	return mintKey("e", existingKeys);
}

export type GraphWorkflowRenameResult = GraphWorkflowCanvas | { readonly error: "collision" | "invalid" };

/**
 * Renaming a key is a GRAPH operation, not a field edit. Without the cascade a rename silently produces
 * `unknownEdgeEndpoint` on the next validate. Pure; the editor swaps in the result.
 *
 * Safe to offer at all because the editor only ever edits a definition and a run pins its own graph copy, so no run is
 * disturbed. It refuses a name that collides with any existing node OR edge key (one namespace) and one that does not
 * match the server's charset.
 */
export function renameNodeKey(
	nodes: readonly GraphWorkflowCanvasNode[],
	edges: readonly GraphWorkflowCanvasEdge[],
	from: string,
	to: string,
): GraphWorkflowRenameResult {
	if (!GRAPH_WORKFLOW_KEY_PATTERN.test(to)) {
		return { error: "invalid" };
	}
	if (to !== from && (nodes.some((node) => node.id === to) || edges.some((edge) => edge.id === to))) {
		return { error: "collision" };
	}
	const pauseKeys = new Set(nodes.filter((node) => node.data.kind === "Pause").map((node) => node.id));
	const renamedNodes = nodes.map((node) => (node.id === from ? { ...node, id: to, data: { ...node.data, key: to } } : node));
	const renamedEdges = edges.map((edge) => {
		const condition = edge.data?.condition;
		// The plan's cascade: a Pause out-edge whose condition value NAMED the node follows the rename. It is a no-op on
		// a well-formed graph, where that value is a decision — and exactly the corruption nobody would find by hand on
		// one that is not.
		const renameValue = condition !== undefined && pauseKeys.has(edge.source) && condition.value === from;
		return {
			...edge,
			source: edge.source === from ? to : edge.source,
			target: edge.target === from ? to : edge.target,
			...(renameValue && condition ? { data: { ...edge.data, condition: { ...condition, value: to } } } : {}),
		};
	});
	return { nodes: renamedNodes, edges: renamedEdges };
}

// ---------------------------------------------------------------------------------------------------------------
// Dirty check
// ---------------------------------------------------------------------------------------------------------------

/** Recursive key sort, so two documents that differ only in member order compare equal. Nulls are preserved. */
function canonicalize(value: unknown): unknown {
	if (Array.isArray(value)) {
		return value.map((entry) => canonicalize(entry));
	}
	if (typeof value === "object" && value !== null) {
		return Object.fromEntries(
			Object.entries(value as Record<string, unknown>)
				.toSorted(([left], [right]) => left.localeCompare(right))
				.map(([key, entry]) => [key, canonicalize(entry)]),
		);
	}
	return value;
}

/** The config with its own `null`/`undefined` members dropped, so `{ resultPath: null }` and `{}` are one graph. Only
 * the TOP level: a null inside the operator's own JSON document is data, and dropping it would hide a real edit. */
function normalizedConfig(config: unknown): unknown {
	const record = typeof config === "object" && config !== null && !Array.isArray(config) ? config : {};
	return canonicalize(
		Object.fromEntries(
			Object.entries(record as Record<string, unknown>).filter(([, value]) => value !== null && value !== undefined),
		),
	);
}

function normalizedGraph(graph: GraphWorkflowGraph | undefined): string {
	const nodes = (graph?.nodes ?? [])
		.map((node) => ({
			key: node.key ?? "",
			kind: node.kind ?? "",
			label: node.label ?? "",
			// A node with NO position is not the same graph as one placed at the origin: the layout makes it dirty, and
			// the first save is what persists the positions it computed.
			position: node.position ? { x: Math.round(node.position.x ?? 0), y: Math.round(node.position.y ?? 0) } : null,
			maxAttempts: node.maxAttempts ?? null,
			timeoutSeconds: node.timeoutSeconds ?? null,
			joinPolicy: narrowGraphWorkflowJoinPolicy(node.joinPolicy),
			config: normalizedConfig(node.config),
		}))
		.toSorted((left, right) => left.key.localeCompare(right.key));
	const edges = (graph?.edges ?? [])
		.map((edge) => ({
			key: edge.key ?? "",
			from: edge.from ?? "",
			to: edge.to ?? "",
			label: edge.label ?? "",
			sourceHandle: edge.sourceHandle ?? null,
			condition: edge.condition
				? {
						path: edge.condition.path ?? null,
						// Normalised, so a stored lowercase `eq` and the canonical `Eq` are the same branch.
						op: normalizeGraphWorkflowConditionOperator(edge.condition.op) ?? edge.condition.op ?? "",
						value: canonicalize(edge.condition.value ?? null),
					}
				: null,
		}))
		.toSorted((left, right) => left.key.localeCompare(right.key));
	return JSON.stringify({ schemaVersion: graph?.schemaVersion ?? 1, nodes, edges });
}

/**
 * Order-independent structural equality, INCLUDING positions and edge label/condition/sourceHandle — a moved node is a
 * real edit here, unlike Preview, because this wire persists `position`. Absent ≡ null ≡ the parser's default
 * (`joinPolicy` `All`, `schemaVersion` 1, `label` ""). Drives the Save button and `useUnsavedChangesGuard`.
 *
 * A graph the EDITOR saved always reads clean on reopen. A terse hand-authored one (no positions, a Pause with no
 * `requireComment`, an End with no `outcome`) reads as dirty the moment it is opened, because opening it fills those in
 * — which is the truth: saving would rewrite the document. That is a Save button that is enabled, not a false diff.
 */
export function graphWorkflowsEqual(a: GraphWorkflowGraph | undefined, b: GraphWorkflowGraph | undefined): boolean {
	return normalizedGraph(a) === normalizedGraph(b);
}
