// The per-form Zod schemas the graph editor's config drawers run at field level. They only produce a field message;
// the structural save gate is GraphWorkflowValidation. Messages are full i18n keys: the form renders `t(issue.message)`.

import { z } from "zod";

import {
	GRAPH_WORKFLOW_KEY_PATTERN,
	graphWorkflowConditionOperators,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { labelsAreValid } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

/**
 * `GraphWorkflowTokens.IsDotPath` verbatim: dot-separated segments, each NON-EMPTY and free of whitespace and the
 * bracket, star and parenthesis characters that would make it a wildcard, an index or a function call (brief §3.1).
 * Deliberately not narrower than the server's — a JSON property may be hyphenated, and refusing one the server accepts
 * blocks a save. The dot is excluded from the segment itself, or `a..b`, `.a` and `a.` all match a rule the server
 * refuses and the field is green on a path the save then 400s.
 *
 * The whitespace is `char.IsWhiteSpace` SPELLED OUT rather than `\s`, because the two sets are not the same one and
 * disagree in both directions: `\s` misses U+0085 (a path the server refuses would be green here) and adds U+FEFF (a
 * path the server accepts would block the save). Neither is a set this client may narrow or widen on its own.
 */
const GRAPH_WORKFLOW_PATH_FORBIDDEN = /[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000[\]*()]/;

/** `IsDotPath` itself: every dot-separated segment non-empty and free of the characters above. */
function isGraphWorkflowDotPath(path: string): boolean {
	return path.split(".").every((segment) => segment.length > 0 && !GRAPH_WORKFLOW_PATH_FORBIDDEN.test(segment));
}

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
		.refine((value) => (value ?? "").trim().length === 0 || isGraphWorkflowDotPath(value ?? ""), {
			message: messageKey(field, "invalid"),
		});
}

/** The fields every node carries, whatever its kind. `maxAttempts` mirrors the server's per-node retry budget. */
export const nodeCommonSchema = z.object({
	key: z.string().regex(GRAPH_WORKFLOW_KEY_PATTERN, { message: messageKey("key", "invalid") }),
	label: z.string(),
	// 100 is a CLIENT-only sanity cap: the server bounds the run through `MaxTotalAttempts`, not per node, so this only
	// keeps a typo out of the field. Raise it freely; nothing on the wire agrees with the number.
	maxAttempts: z
		.number()
		.int({ message: messageKey("maxAttempts", "range") })
		.min(1, { message: messageKey("maxAttempts", "range") })
		.max(100, { message: messageKey("maxAttempts", "range") })
		.optional(),
	timeoutSeconds: z
		.number()
		.int({ message: messageKey("timeoutSeconds", "min") })
		.min(1, { message: messageKey("timeoutSeconds", "min") })
		.nullable()
		.optional(),
});

// Mirrors GraphWorkflowGraph.ParseAliases and GraphWorkflowIntentService.Normalize: whitespace collapsed, trailing
// `.!? ` dropped, compared ignoring case.
const MAX_ALIASES = 32;
const MAX_ALIAS_LENGTH = 120;

function normalizeAlias(alias: string): string {
	return alias
		.split(/\s+/)
		.filter((word) => word.length > 0)
		.join(" ")
		.replace(/[.!? ]+$/, "");
}

const aliasesSchema = z.array(z.string()).superRefine((aliases, context) => {
	if (aliases.length > MAX_ALIASES) {
		context.addIssue({ code: "custom", message: messageKey("aliases", "tooMany") });
		return;
	}
	const seen = new Set<string>();
	for (const alias of aliases) {
		const trimmed = alias.trim();
		const normalized = normalizeAlias(trimmed).toLowerCase();
		if (trimmed.length === 0 || trimmed.length > MAX_ALIAS_LENGTH || normalized.length === 0) {
			context.addIssue({ code: "custom", message: messageKey("aliases", "invalid") });
			return;
		}
		if (seen.has(normalized)) {
			context.addIssue({ code: "custom", message: messageKey("aliases", "duplicate") });
			return;
		}
		seen.add(normalized);
	}
});

export const startConfigSchema = z.object({
	inputSchema: jsonObjectText("inputSchema"),
	defaultInput: z
		.string()
		.nullable()
		.refine((value) => parsesTo("json", value), { message: messageKey("defaultInput", "invalidJson") }),
	aliases: aliasesSchema,
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

export const llmCallConfigSchema = z.object({
	prompt: z
		.string()
		.trim()
		.min(1, { message: messageKey("llmPrompt", "required") }),
	responseJsonSchema: jsonObjectText("responseJsonSchema"),
	inputBindings: z
		.array(
			z.object({
				parameter: z
					.string()
					.trim()
					.min(1, { message: messageKey("inputBindings", "invalid") }),
				path: z
					.string()
					.trim()
					.min(1, { message: messageKey("inputBindings", "invalid") })
					.refine(isGraphWorkflowDotPath, { message: messageKey("inputBindings", "invalid") }),
			}),
		)
		.superRefine((bindings, context) => {
			const names = new Set<string>();
			for (const binding of bindings) {
				if (names.has(binding.parameter)) {
					context.addIssue({ code: "custom", message: messageKey("inputBindings", "invalid") });
				}
				names.add(binding.parameter);
			}
		}),
});

/** The one tool a Tool node may run against a link allow-list (ADR 0017); the server's save gate says the same. */
export const webFetchToolName = "web_fetch";

/** An absolute http or https URL, parsed; `undefined` for anything else. */
function parseHttpUrl(value: string): URL | undefined {
	try {
		const url = new URL(value.trim());
		return url.protocol === "http:" || url.protocol === "https:" ? url : undefined;
	} catch {
		return undefined;
	}
}

/** An allowed link as the server's gate reads it: an absolute http or https URL. */
function isHttpUrl(value: string): boolean {
	return parseHttpUrl(value) !== undefined;
}

/**
 * `WebFetchService.IsAllowed`: same scheme, host and port, and a path under an entry's path on a segment boundary, so
 * "/docs" admits "/docs" and "/docs/x" but not "/docs-private".
 */
function isUnderAllowedUrl(url: URL, allowedUrls: readonly string[]): boolean {
	return allowedUrls.some((entry) => {
		const allowed = parseHttpUrl(entry);
		if (!allowed || allowed.protocol !== url.protocol || allowed.hostname !== url.hostname || allowed.port !== url.port) {
			return false;
		}
		const prefix = allowed.pathname;
		const path = url.pathname;
		return path.startsWith(prefix) && (path.length === prefix.length || prefix.endsWith("/") || path[prefix.length] === "/");
	});
}

/** The literal `url` argument the author typed, or `undefined` when there is none (or the JSON does not parse). */
function literalUrlArgument(argumentsJson: string | null | undefined): { value: unknown } | undefined {
	try {
		const parsed: unknown = JSON.parse((argumentsJson ?? "").trim());
		return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed) && "url" in parsed
			? { value: (parsed as { url: unknown }).url }
			: undefined;
	} catch {
		return undefined;
	}
}

export const toolConfigSchema = z
	.object({
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
		allowedUrls: z.array(z.string()),
	})
	.superRefine((node, context) => {
		// Mirrors the save gate's web_fetch rules so the node shows them before a save round-trip does.
		if (node.toolName !== webFetchToolName) {
			return;
		}
		if (node.allowedUrls.length === 0) {
			context.addIssue({ code: "custom", path: ["allowedUrls"], message: messageKey("allowedUrls", "required") });
		} else if (!node.allowedUrls.every(isHttpUrl)) {
			context.addIssue({ code: "custom", path: ["allowedUrls"], message: messageKey("allowedUrls", "invalid") });
		} else {
			const literal = literalUrlArgument(node.argumentsJson);
			const url = typeof literal?.value === "string" ? parseHttpUrl(literal.value) : undefined;
			if (literal && !(url && isUnderAllowedUrl(url, node.allowedUrls))) {
				context.addIssue({ code: "custom", path: ["allowedUrls"], message: messageKey("allowedUrls", "literalUrlNotAllowed") });
			}
		}
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

export const chatInputConfigSchema = z.object({
	prompt: z
		.string()
		.trim()
		.min(1, { message: messageKey("prompt", "required") }),
});

/** The drawer's half of `decisionShapeIssues`, keyed to the field that has to change. Hidden fields (`noul` labels) stay silent. */
export const decisionModelConfigSchema = z
	.object({
		question: z
			.string()
			.trim()
			.min(1, { message: messageKey("question", "required") }),
		questionType: z.string().nullable(),
		labels: z.array(z.string()),
		criteria: z.record(z.string(), z.string()),
		provider: z.string().nullable(),
		model: z.string().nullable(),
		inputBindings: llmCallConfigSchema.shape.inputBindings,
	})
	.superRefine((node, context) => {
		const type = node.questionType ?? "choice";
		const provider = node.provider ?? "llm";
		if (type === "choice" && !labelsAreValid(node.labels)) {
			context.addIssue({ code: "custom", path: ["labels"], message: messageKey("labels", "invalid") });
		}
		if (type === "score" && !labelsAreValid(node.labels, 10)) {
			context.addIssue({ code: "custom", path: ["labels"], message: messageKey("labels", "levelsInvalid") });
		}
		if (provider === "llm" && (type === "noul" || type === "score")) {
			context.addIssue({ code: "custom", path: ["questionType"], message: messageKey("questionType", "unsupportedByProvider") });
		}
		if (provider === "systemone" && (node.model ?? "").trim().length === 0) {
			context.addIssue({ code: "custom", path: ["model"], message: messageKey("model", "decisionRequired") });
		}
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
