// Wire ↔ canvas mapping for the graph editor. Pure: no React.
//
// Three JSON-shaped fields (`defaultInput`, `responseJsonSchema`, `argumentsJson`) and every edge condition `value` are
// held on the canvas as STRINGS, not parsed objects. A half-typed JSON object is not representable as an object, and
// re-serializing on every keystroke destroys the operator's formatting. They are parsed once, at `canvasToGraph` time,
// and a parse failure becomes a `GraphWorkflowGraphIssue` rather than a thrown render.

import {
	type GraphWorkflowArgumentBinding,
	type GraphWorkflowAttachmentFlags,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasEdgeCondition,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowGraphSettings,
	type GraphWorkflowInputBinding,
	type GraphWorkflowLoadedCanvas,
	type GraphWorkflowSamplingOptions,
	graphWorkflowNodeTypeByKind,
	standardGraphSettings,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { layoutGraphWorkflow } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import {
	configRecord,
	type GraphWorkflowGraph,
	type GraphWorkflowGraphEdge,
	type GraphWorkflowGraphNode,
	type GraphWorkflowNodeKind,
	graphWorkflowPauseDecisionKinds,
	narrowGraphWorkflowDefinitionKind,
	narrowGraphWorkflowJoinPolicy,
	narrowGraphWorkflowNodeKind,
	normalizeGraphWorkflowConditionOperator,
	stringOrEmpty,
	toGraphWorkflowDecisionKinds,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

// ---------------------------------------------------------------------------------------------------------------
// Reading the wire
// ---------------------------------------------------------------------------------------------------------------

function stringOrNull(value: unknown): string | null {
	return typeof value === "string" ? value : null;
}

/** A DecisionModel's `criteria` map; non-string descriptions are dropped (the server takes label → text only). */
function criteriaFromWire(value: unknown): Readonly<Record<string, string>> {
	if (value === null || typeof value !== "object" || Array.isArray(value)) {
		return {};
	}
	return Object.fromEntries(Object.entries(value).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
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
				aliases: Array.isArray(config["aliases"])
					? config["aliases"].filter((entry): entry is string => typeof entry === "string")
					: [],
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
				allowedUrls: Array.isArray(config["allowedUrls"])
					? config["allowedUrls"].filter((entry): entry is string => typeof entry === "string")
					: [],
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
				questionType: stringOrNull(config["questionType"]),
				labels: Array.isArray(config["labels"])
					? config["labels"].filter((entry): entry is string => typeof entry === "string")
					: [],
				criteria: criteriaFromWire(config["criteria"]),
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
export function graphSettingsFromWire(graph: GraphWorkflowGraph | undefined): GraphWorkflowGraphSettings {
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
 * lenient: text that is not JSON saves as the string it is, so typing `Approve` still works.
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
 * (or the Preview importer) carries none — belt and braces, it is re-derived from the label, then from the condition value,
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
 * The wire graph as React Flow nodes and edges. A node WITHOUT a position is laid out (`position` is optional, and a
 * laid-out node is dirty by construction, so the first save persists what the layout computed); a node
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
				// Absent rather than empty: the server takes 1 to 32 aliases or none at all.
				...(data.aliases.length > 0 ? { aliases: data.aliases.map((alias) => alias.trim()) } : {}),
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
				...(data.allowedUrls.length > 0 ? { allowedUrls: data.allowedUrls } : {}),
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
			// Labels stay on the canvas when the type flips to `noul`, so flipping back restores them, but the wire must not
			// carry them (`decisionNoulHasLabels`). Criteria likewise travel for `choice` only, and only when one is set.
			const type = data.questionType ?? "choice";
			// An imported key that names no label has no row to edit it in, so it is dropped here rather than leaving
			// the node unsaveable on `decisionCriteriaKeyUnknown`.
			const criteria = Object.entries(data.criteria).filter(
				([label, description]) => data.labels.includes(label) && description.trim().length > 0,
			);
			return {
				question: data.question,
				...(data.questionType === null ? {} : { questionType: data.questionType }),
				...(type === "noul" ? {} : { labels: data.labels }),
				...(type === "choice" && criteria.length > 0 ? { criteria: Object.fromEntries(criteria) } : {}),
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
 * string `Approve`, and `true` a boolean. Deliberately LENIENT while `conditionValueText` writes strict
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
