// Wire ↔ canvas mapping for the graph editor. Pure: no React, and only React Flow's `Node`/`Edge` TYPES.
//
// Node data is a DISCRIMINATED UNION on `kind`. With eight kinds and four of them carrying real config, the flat
// alternative — one interface holding every agent, tool, pause and condition field as optional — is an untyped
// grab-bag that no reader can narrow and no compiler can check a card against.
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

import type { ChatSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	type GraphWorkflowConditionOperator,
	type GraphWorkflowDecisionKind,
	type GraphWorkflowDefinitionKind,
	type GraphWorkflowFailureClass,
	type GraphWorkflowGraph,
	type GraphWorkflowGraphEdge,
	type GraphWorkflowGraphNode,
	type GraphWorkflowJoinPolicy,
	type GraphWorkflowNodeKind,
	type GraphWorkflowNodeRunStatus,
	graphWorkflowDefaultMaxAttempts,
	graphWorkflowPauseDecisionKinds,
	narrowGraphWorkflowDefinitionKind,
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

export type GraphWorkflowInputBinding = GraphWorkflowArgumentBinding;
export type GraphWorkflowSamplingOptions = Omit<ChatSamplingOptions, "seed"> & { seed?: string };

/**
 * The chat flags. `undefined` is "absent on the wire", which the parser reads as its own default (`false`, or `true`
 * for an End in a Chat graph) — held as absent rather than defaulted so a Standard graph saves byte for byte as it was.
 */
interface GraphWorkflowChatFlags {
	readonly publishToChat?: boolean;
}

interface GraphWorkflowAttachmentFlags extends GraphWorkflowChatFlags {
	readonly includeAttachments?: boolean;
}

/** The graph-level `chat` block, member by member as stored: an absent member is the parser's default. */
export interface GraphWorkflowChatSettings {
	readonly acceptsAttachments?: boolean;
	readonly requireRerunConfirmation?: boolean;
}

/** What the graph says about itself, outside its nodes and edges. `chat` is only written for a Chat graph. */
export interface GraphWorkflowGraphSettings {
	readonly kind: GraphWorkflowDefinitionKind;
	readonly chat?: GraphWorkflowChatSettings;
}

export const standardGraphSettings: GraphWorkflowGraphSettings = { kind: "Standard" };

/** The parser's defaults for an absent `chat` member. */
export const graphWorkflowChatDefaults = { acceptsAttachments: false, requireRerunConfirmation: true } as const;

export type GraphWorkflowCanvasNodeData =
	| (GraphWorkflowNodeBase & {
			readonly kind: "Start";
			readonly inputSchema: string | null;
			readonly defaultInput: string | null;
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "LlmCall";
			readonly model: string | null;
			readonly systemPrompt: string | null;
			readonly prompt: string;
			readonly inputBindings: readonly GraphWorkflowInputBinding[];
			readonly reasoningEffort: string | null;
			readonly responseJsonSchema: string | null;
			readonly samplingOptions: GraphWorkflowSamplingOptions;
	  } & GraphWorkflowAttachmentFlags)
	| (GraphWorkflowNodeBase & {
			readonly kind: "Agent";
			readonly agentDefinitionId: string | null;
			readonly instructions: string;
			readonly model: string | null;
			readonly reasoningEffort: string | null;
			readonly responseJsonSchema: string | null;
			readonly includeUpstreamOutputs: boolean;
	  } & GraphWorkflowAttachmentFlags)
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
	| (GraphWorkflowNodeBase & { readonly kind: "ChatInput"; readonly prompt: string })
	| (GraphWorkflowNodeBase & {
			readonly kind: "DecisionModel";
			readonly question: string;
			readonly labels: readonly string[];
			readonly provider: string | null;
			readonly model: string | null;
			readonly inputBindings: readonly GraphWorkflowInputBinding[];
	  })
	| (GraphWorkflowNodeBase & {
			readonly kind: "End";
			readonly outcome: string;
			readonly resultPath: string | null;
	  } & GraphWorkflowChatFlags);

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

/** A canvas read off a wire graph, with the graph-level settings that are not nodes or edges. */
export interface GraphWorkflowLoadedCanvas extends GraphWorkflowCanvas {
	readonly settings: GraphWorkflowGraphSettings;
}

/** React Flow type keys — the keys the canvas registers in its `nodeTypes` map, one component per kind. */
export const graphWorkflowNodeTypeByKind: Record<GraphWorkflowNodeKind, string> = {
	Start: "start",
	Agent: "agent",
	LlmCall: "llm-call",
	Tool: "tool",
	Condition: "condition",
	Parallel: "parallel",
	Join: "join",
	Pause: "pause",
	End: "end",
	ChatInput: "chat-input",
	DecisionModel: "decision-model",
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

function booleanOrUndefined(value: unknown): boolean | undefined {
	return typeof value === "boolean" ? value : undefined;
}

/** Only the members that are present, so an absent flag stays absent through the round trip. */
function chatFlagsFromWire(config: Record<string, unknown>, withAttachments: boolean): GraphWorkflowAttachmentFlags {
	const publishToChat = booleanOrUndefined(config["publishToChat"]);
	const includeAttachments = withAttachments ? booleanOrUndefined(config["includeAttachments"]) : undefined;
	return {
		...(publishToChat === undefined ? {} : { publishToChat }),
		...(includeAttachments === undefined ? {} : { includeAttachments }),
	};
}

function chatFlagsToWire(data: GraphWorkflowAttachmentFlags): GraphWorkflowAttachmentFlags {
	return {
		...(data.publishToChat === undefined ? {} : { publishToChat: data.publishToChat }),
		...(data.includeAttachments === undefined ? {} : { includeAttachments: data.includeAttachments }),
	};
}

function numberOrUndefined(value: unknown): number | undefined {
	return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/**
 * A JSON-shaped wire member as editable text. Every value is written as JSON — an object or array pretty-printed, a
 * string QUOTED — because the text this returns is what `parseJsonField` reads back. A string rendered verbatim
 * (`abc` for the stored `"abc"`) does not parse, so a `defaultInput` the server accepts became a permanent
 * `invalidJson` issue on a definition nobody had edited; quoting it makes the round trip lossless and leaves the
 * "must be an object" members (`inputSchema`, `responseJsonSchema`, `arguments`) reporting the shape they really have.
 */
function jsonText(value: unknown): string | null {
	if (value === undefined || value === null) {
		return null;
	}
	return JSON.stringify(value, null, 2) ?? null;
}

function bindingsFromWire(value: unknown): readonly GraphWorkflowArgumentBinding[] {
	return Object.entries(configRecord(value)).flatMap(([parameter, path]) =>
		typeof path === "string" ? [{ parameter, path }] : [],
	);
}

function samplingFromWire(value: unknown): GraphWorkflowSamplingOptions {
	const source = configRecord(value);
	const result: GraphWorkflowSamplingOptions = {};
	for (const key of [
		"temperature",
		"topP",
		"topK",
		"minP",
		"maxOutputTokens",
		"repeatPenalty",
		"repeatLastN",
		"presencePenalty",
		"frequencyPenalty",
		"numCtx",
	] as const) {
		const number = numberOrUndefined(source[key]);
		if (number !== undefined) {
			result[key] = number;
		}
	}
	if (Array.isArray(source["stop"])) {
		result.stop = source["stop"].filter((entry): entry is string => typeof entry === "string");
	}
	if (typeof source["seed"] === "string") {
		result.seed = source["seed"];
	}
	return result;
}

function samplingToWire(value: GraphWorkflowSamplingOptions): GraphWorkflowSamplingOptions | undefined {
	const result = Object.fromEntries(
		Object.entries(value).flatMap(([key, entry]) => {
			if (key === "stop" && Array.isArray(entry)) {
				const stop = entry.filter((item) => item.length > 0);
				return stop.length > 0 ? [[key, stop]] : [];
			}
			return entry !== undefined && entry !== "" ? [[key, entry]] : [];
		}),
	) as GraphWorkflowSamplingOptions;
	return Object.keys(result).length > 0 ? result : undefined;
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
				...chatFlagsFromWire(config, true),
			};
		case "LlmCall":
			return {
				...base,
				kind: "LlmCall",
				model: stringOrNull(config["model"]),
				systemPrompt: stringOrNull(config["systemPrompt"]),
				prompt: stringOrEmpty(config["prompt"]),
				inputBindings: bindingsFromWire(config["inputBindings"]),
				reasoningEffort: stringOrNull(config["reasoningEffort"]),
				responseJsonSchema: jsonText(config["responseJsonSchema"]),
				samplingOptions: samplingFromWire(config["samplingOptions"]),
				...chatFlagsFromWire(config, true),
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
		case "ChatInput":
			return { ...base, kind: "ChatInput", prompt: stringOrEmpty(config["prompt"]) };
		case "DecisionModel":
			return {
				...base,
				kind: "DecisionModel",
				question: stringOrEmpty(config["question"]),
				labels: Array.isArray(config["labels"])
					? config["labels"].filter((entry): entry is string => typeof entry === "string")
					: [],
				provider: stringOrNull(config["provider"]),
				model: stringOrNull(config["model"]),
				inputBindings: bindingsFromWire(config["inputBindings"]),
			};
		// `default` IS the End case: `narrowGraphWorkflowNodeKind` answers one of the members and the others above
		// are handled, so TypeScript narrows `node` here exactly as a `case "End"` would. It also catches an UNKNOWN
		// kind, which the narrowing turns into `End` — lossy, and `loadedGraphIssues` is what keeps that visible.
		default:
			return {
				...base,
				kind: "End",
				outcome: stringOrEmpty(config["outcome"]),
				resultPath: stringOrNull(config["resultPath"]),
				...chatFlagsFromWire(config, false),
			};
	}
}

/** The graph-level `kind` and `chat` block. A `chat` member that is not a boolean reads as absent (the default). */
function graphSettingsFromWire(graph: GraphWorkflowGraph | undefined): GraphWorkflowGraphSettings {
	const kind = narrowGraphWorkflowDefinitionKind(graph?.kind);
	const raw: unknown = graph?.chat;
	if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
		return { kind };
	}
	const record = raw as Record<string, unknown>;
	const acceptsAttachments = booleanOrUndefined(record["acceptsAttachments"]);
	const requireRerunConfirmation = booleanOrUndefined(record["requireRerunConfirmation"]);
	return {
		kind,
		chat: {
			...(acceptsAttachments === undefined ? {} : { acceptsAttachments }),
			...(requireRerunConfirmation === undefined ? {} : { requireRerunConfirmation }),
		},
	};
}

/**
 * A wire condition `value` as canvas text: its JSON, so a string reads QUOTED — `"Approve"`, not `Approve`.
 *
 * The quotes are what keep the operand's TYPE through an open-and-save. `conditionValueToWire` parses this text as
 * JSON, so rendering a string raw handed back the stored strings `"true"`, `"123"` and `"null"` as a boolean, a number
 * and null — a different branch than the one the author stored, on an edit that touched nothing. Writing is still
 * lenient (ruling F5-3): text that is not JSON saves as the string it is, so typing `Approve` still works.
 */
function conditionValueText(value: unknown): string {
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
		// guessing `Eq` and silently rewriting the branch on the next save. That makes the canvas LOSSY, so the loader
		// runs `loadedGraphIssues` over the wire graph it just read and holds the resulting
		// `unknownConditionOperator` issue open until a different graph is loaded, blocking Save on a document this
		// client cannot faithfully write back. The server's parser is the authority on what the token meant.
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
	// The WIRE operand, never the canvas text: that text is JSON, so a decision reads `"Approve"` there and a boolean
	// branch and a `"true"` string are one and the same token again. The handle is derived from what was stored.
	const operand: unknown = edge.condition?.value;
	if (sourceKind === "Condition") {
		if (label === "true" || label === "false") {
			return label;
		}
		if (condition?.op !== "Eq") {
			return undefined;
		}
		return operand === true || operand === "true" ? "true" : operand === false || operand === "false" ? "false" : undefined;
	}
	if (sourceKind === "Pause") {
		const decisions: readonly string[] = graphWorkflowPauseDecisionKinds;
		if (decisions.includes(label)) {
			return label;
		}
		return condition !== undefined && typeof operand === "string" && decisions.includes(operand) ? operand : undefined;
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
): GraphWorkflowLoadedCanvas {
	const wireNodes = graph?.nodes ?? [];
	const wireEdges = graph?.edges ?? [];
	const kindByKey = new Map<string, GraphWorkflowNodeKind>(
		wireNodes.map((node) => [node.key ?? "", narrowGraphWorkflowNodeKind(node.kind)]),
	);
	// Only laid out when something actually needs a position: the common case is a definition this editor saved, where
	// every node carries one and the ranking would be thrown away.
	const layout =
		options?.relayout === true || wireNodes.some((node) => !node.position)
			? layoutGraphWorkflow(
					wireNodes.map((node) => ({ key: node.key ?? "" })),
					wireEdges.map((edge) => ({ from: edge.from ?? "", to: edge.to ?? "" })),
				)
			: undefined;

	const nodes = wireNodes.map((node): GraphWorkflowCanvasNode => {
		const data = nodeDataFromWire(node);
		const stored = node.position;
		const placed = layout?.positions.get(data.key);
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

	return { nodes, edges, settings: graphSettingsFromWire(graph) };
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

function llmBindingsToWire(
	bindings: readonly GraphWorkflowInputBinding[],
	key: string,
	issues: GraphWorkflowGraphIssue[],
): Record<string, string> | undefined {
	const names = new Set<string>();
	for (const binding of bindings) {
		const invalidPath = binding.path.split(".").some((segment) => segment.length === 0 || /\s|[[\]*()]/u.test(segment));
		if (binding.parameter.trim().length === 0 || invalidPath || names.has(binding.parameter)) {
			issues.push({ rule: "invalidInputBindings", subject: key });
			return undefined;
		}
		names.add(binding.parameter);
	}
	return bindingsToWire(bindings);
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
				...chatFlagsToWire(data),
			};
		case "LlmCall": {
			const bindings = llmBindingsToWire(data.inputBindings, data.key, issues);
			const samplingOptions = samplingToWire(data.samplingOptions);
			return {
				...(data.model === null ? {} : { model: data.model }),
				...(data.systemPrompt === null ? {} : { systemPrompt: data.systemPrompt }),
				prompt: data.prompt,
				...(bindings ? { inputBindings: bindings } : {}),
				...(data.reasoningEffort === null ? {} : { reasoningEffort: data.reasoningEffort }),
				...(data.responseJsonSchema === null
					? {}
					: { responseJsonSchema: parseJsonField(data.responseJsonSchema, data.key, issues, true) }),
				...(samplingOptions ? { samplingOptions } : {}),
				...chatFlagsToWire(data),
			};
		}
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
		case "ChatInput":
			return { prompt: data.prompt };
		case "DecisionModel": {
			const bindings = llmBindingsToWire(data.inputBindings, data.key, issues);
			return {
				question: data.question,
				labels: data.labels,
				...(data.provider === null ? {} : { provider: data.provider }),
				...(data.model === null ? {} : { model: data.model }),
				...(bindings ? { inputBindings: bindings } : {}),
			};
		}
		// `default` IS the End case — see `nodeDataFromWire`.
		default:
			return { outcome: data.outcome, resultPath: data.resultPath, ...chatFlagsToWire(data) };
	}
}

/**
 * A canvas condition value back to JSON, falling back to the raw string — so `"Approve"` and `Approve` both stay the
 * string `Approve`, and `true` a boolean. Deliberately LENIENT (ruling F5-3) while `conditionValueText` writes strict
 * JSON: the field reads back what it rendered, and an operator who types an unquoted word still gets a string.
 */
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
	settings: GraphWorkflowGraphSettings = standardGraphSettings,
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

	// A Standard graph writes neither member, so it saves byte for byte as it did before chat graphs existed. The `chat`
	// block is written only when the graph carries one: an absent block is the parser's defaults.
	const graphLevel =
		settings.kind === "Chat" ? { kind: "Chat" as const, ...(settings.chat === undefined ? {} : { chat: settings.chat }) } : {};
	return { graph: { schemaVersion: 1, ...graphLevel, nodes: graphNodes, edges: graphEdges }, issues };
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
		case "LlmCall":
			return {
				...base,
				kind,
				model: null,
				systemPrompt: null,
				prompt: "",
				inputBindings: [],
				reasoningEffort: null,
				responseJsonSchema: null,
				samplingOptions: {},
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
		case "ChatInput":
			return { ...base, kind, prompt: "" };
		case "DecisionModel":
			return { ...base, kind, question: "", labels: [], provider: null, model: null, inputBindings: [] };
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
 * The `context` edges this graph is missing around its Pause nodes — the authoring half of the affordance S4 gave the
 * importer (`CanvasWorkflowImport.AddPauseContextEdges`, wiki page 21 §5).
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
 *     policy is read off the NODE whatever its kind: `Admission` honours it on an `End` or an `Agent` too, and
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
	// A ChatInput parks like a Pause and hands on only its answer, so it is walked exactly as one (plan §3.2).
	const pauseKeys = new Set(
		nodes.filter((node) => node.data.kind === "Pause" || node.data.kind === "ChatInput").map((node) => node.id),
	);
	// EVERY kind, not just `Join`: the policy is a member of the node base and the run reads it off whichever node it
	// is admitting. An `End` or an `Agent` set to `Any` joins exactly as a `Join` does.
	const anyPolicy = new Set(nodes.filter((node) => node.data.joinPolicy === "Any").map((node) => node.id));
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
		if (!inbound.every((pair) => pauseKeys.has(pair.from)) || anyPolicy.has(successor)) {
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
	const renamedNodes = nodes.map((node) => (node.id === from ? { ...node, id: to, data: { ...node.data, key: to } } : node));
	// Endpoints only. An edge condition is never rewritten: a Pause out-edge's `condition.value` is a DECISION name, so
	// following a rename into it could only corrupt the routing of a node someone happened to key `Approve`.
	const renamedEdges = edges.map((edge) => ({
		...edge,
		source: edge.source === from ? to : edge.source,
		target: edge.target === from ? to : edge.target,
	}));
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
	// Absent `kind` and `Standard` are one graph; the `chat` block compares member by member as stored.
	const settings = graphSettingsFromWire(graph);
	return JSON.stringify({
		schemaVersion: graph?.schemaVersion ?? 1,
		kind: settings.kind,
		chat: settings.chat === undefined ? null : canonicalize(settings.chat),
		nodes,
		edges,
	});
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
