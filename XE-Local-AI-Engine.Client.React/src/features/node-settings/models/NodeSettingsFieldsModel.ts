import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
	XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest,
} from "@/core/api/generated";

// The migrated appsettings knobs as editable form state. Numbers are kept as `number | string` so an in-progress
// edit (an empty input, a partial number) survives in the controlled NumberInput without being coerced; validation
// resolves the string at save time. Each field is schema-validated against the server-provided Min/Max bounds before
// being sent, and only changed fields are included in the PUT (optional request semantics: omit = keep current).

// Numeric bounds carried by the GET response for a field. The response exposes per-field min/max for the
// range-validated numbers (mirrors the existing timeout min/max pattern).
export interface NumericBounds {
	readonly min: number;
	readonly max: number;
}

// Hardcoded fallback bounds matching the backend Normalize ranges (used when the response omits a bound on an old
// server). Mirrors the node-settings server contract.
export const nodeSettingsFieldBounds = {
	llamaMaxLoadedProcesses: { min: 1, max: 16 },
	llamaIdleTimeToLiveSeconds: { min: 30, max: 86400 },
	maxResponseSizeMb: { min: 1, max: 100 },
	orchestrationIdleTimeoutSeconds: { min: 1, max: 3600 },
	agentHomeTimeoutSeconds: { min: 1, max: 86400 },
	maxPendingToolCallAgeMinutes: { min: 1, max: 60 },
} as const satisfies Record<string, NumericBounds>;

// Recommended llama.cpp tag must match the upstream release-tag scheme `b<N>` (e.g. b9692). Enforced at every entry
// point (settings save, update endpoint, catalog, manager) to prevent path/URL injection into the download URL.
export const llamaCppTagPattern = /^b\d+$/;

// Resolves a controlled numeric input (number or string) to a valid integer within [min, max], or undefined when the
// value is empty / fractional / out of range. Mirrors `toValidNodeSettingsTimeoutSeconds`.
export function toValidBoundedInt(value: number | string, bounds: NumericBounds): number | undefined {
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isInteger(numeric) || numeric < bounds.min || numeric > bounds.max) {
		return undefined;
	}
	return numeric;
}

// Validates the recommended llama.cpp tag. Empty resolves to undefined (the field is left unchanged on save).
export function validateLlamaCppTag(value: string): { value?: string; error?: "format" } {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return {};
	}
	return llamaCppTagPattern.test(trimmed) ? { value: trimmed } : { error: "format" };
}

// http/https URL validator for the Ollama endpoint. Empty resolves to undefined (left unchanged on save).
const httpUrlSchema = z
	.string()
	.trim()
	.url()
	.refine((value) => /^https?:\/\//i.test(value), { message: "protocol" });

export function validateOllamaEndpoint(value: string): { value?: string; error?: "url" } {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return {};
	}
	const parsed = httpUrlSchema.safeParse(trimmed);
	return parsed.success ? { value: parsed.data } : { error: "url" };
}

// A single tool-capable-model name. Models are referenced by their provider model name; reject blank/whitespace
// entries and obvious control characters so the list editor never persists junk rows.
// biome-ignore lint/suspicious/noControlCharactersInRegex: intentionally rejects ASCII control chars in a model name.
const controlCharPattern = /[\u0000-\u001f]/;
const toolCapableModelNameSchema = z
	.string()
	.trim()
	.min(1)
	.refine((value) => !controlCharPattern.test(value), { message: "control-char" });

export function validateToolCapableModels(values: readonly string[]): { value: string[]; hasInvalid: boolean } {
	const cleaned: string[] = [];
	let hasInvalid = false;
	for (const raw of values) {
		const parsed = toolCapableModelNameSchema.safeParse(raw);
		if (parsed.success) {
			cleaned.push(parsed.data);
		} else if (raw.trim().length > 0) {
			// A non-empty but invalid entry is a hard error; a purely empty row is silently dropped.
			hasInvalid = true;
		}
	}
	return { value: cleaned, hasInvalid };
}

// The editable form state for the migrated node-settings fields. Numbers stay as `number | string` (in-progress edit
// support); the model name / endpoint / quant are plain strings; the tool-capable list is a string array. Developer-
// only fields are present here but only rendered behind the developer-mode gate on the page.
export interface NodeSettingsFieldsForm {
	defaultModelName: string;
	enableTools: boolean;
	toolCapableModels: string[];
	ollamaEndpoint: string;
	huggingFaceDefaultQuant: string;
	recommendedLlamaCppTag: string;
	llamaMaxLoadedProcesses: number | string;
	llamaIdleTimeToLiveSeconds: number | string;
	maxResponseSizeMb: number | string;
	// Developer-only
	orchestrationIdleTimeoutSeconds: number | string;
	agentHomePrepareTimeoutSeconds: number | string;
	agentHomeCommandTimeoutSeconds: number | string;
	agentHomeMaxSelectedFolderBytes: number | string;
	agentHomeMaxPatchBytes: number | string;
	maxPendingToolCallAgeMinutes: number | string;
}

// Defaults used when the response omits a field. These mirror the backend seed defaults (the `Default*` consts in
// StoredNodeSettings.cs) so the form renders sensible values on an old server that has not yet persisted the field.
export const nodeSettingsFieldDefaults: NodeSettingsFieldsForm = {
	defaultModelName: "",
	enableTools: true,
	toolCapableModels: [],
	ollamaEndpoint: "",
	huggingFaceDefaultQuant: "",
	recommendedLlamaCppTag: "",
	llamaMaxLoadedProcesses: 3,
	llamaIdleTimeToLiveSeconds: 900,
	maxResponseSizeMb: 10,
	orchestrationIdleTimeoutSeconds: 120,
	agentHomePrepareTimeoutSeconds: 900,
	agentHomeCommandTimeoutSeconds: 300,
	agentHomeMaxSelectedFolderBytes: 536870912,
	agentHomeMaxPatchBytes: 52428800,
	maxPendingToolCallAgeMinutes: 10,
};

// Coalesces a nullable numeric response field into a form value, falling back to the provided default when absent.
function numberOr(value: number | null | undefined, fallback: number | string): number | string {
	return value ?? fallback;
}

// Maps the GET response into the editable form state. A null/absent field falls back to its seed default so the form
// always renders a concrete value.
export function toNodeSettingsFieldsForm(response: NodeSettingsResponse | undefined): NodeSettingsFieldsForm {
	if (!response) {
		return { ...nodeSettingsFieldDefaults };
	}
	return {
		defaultModelName: response.defaultModelName ?? "",
		enableTools: response.enableTools ?? nodeSettingsFieldDefaults.enableTools,
		toolCapableModels: response.toolCapableModels ? [...response.toolCapableModels] : [],
		ollamaEndpoint: response.ollamaEndpoint ?? "",
		huggingFaceDefaultQuant: response.huggingFaceDefaultQuant ?? "",
		recommendedLlamaCppTag: response.recommendedLlamaCppTag ?? "",
		llamaMaxLoadedProcesses: numberOr(response.llamaMaxLoadedProcesses, nodeSettingsFieldDefaults.llamaMaxLoadedProcesses),
		llamaIdleTimeToLiveSeconds: numberOr(
			response.llamaIdleTimeToLiveSeconds,
			nodeSettingsFieldDefaults.llamaIdleTimeToLiveSeconds,
		),
		maxResponseSizeMb: numberOr(response.maxResponseSizeMb, nodeSettingsFieldDefaults.maxResponseSizeMb),
		orchestrationIdleTimeoutSeconds: numberOr(
			response.orchestrationIdleTimeoutSeconds,
			nodeSettingsFieldDefaults.orchestrationIdleTimeoutSeconds,
		),
		agentHomePrepareTimeoutSeconds: numberOr(
			response.agentHomePrepareTimeoutSeconds,
			nodeSettingsFieldDefaults.agentHomePrepareTimeoutSeconds,
		),
		agentHomeCommandTimeoutSeconds: numberOr(
			response.agentHomeCommandTimeoutSeconds,
			nodeSettingsFieldDefaults.agentHomeCommandTimeoutSeconds,
		),
		agentHomeMaxSelectedFolderBytes: numberOr(
			response.agentHomeMaxSelectedFolderBytes,
			nodeSettingsFieldDefaults.agentHomeMaxSelectedFolderBytes,
		),
		agentHomeMaxPatchBytes: numberOr(response.agentHomeMaxPatchBytes, nodeSettingsFieldDefaults.agentHomeMaxPatchBytes),
		maxPendingToolCallAgeMinutes: numberOr(
			response.maxPendingToolCallAgeMinutes,
			nodeSettingsFieldDefaults.maxPendingToolCallAgeMinutes,
		),
	};
}

// Resolves the effective bounds for a field, preferring the server-provided value and falling back to the hardcoded
// default range.
function boundsOf(min: number | undefined, max: number | undefined, fallback: NumericBounds): NumericBounds {
	return { min: min ?? fallback.min, max: max ?? fallback.max };
}

// The bounds the form needs to validate + render ranges, resolved from the response (server-authoritative) with a
// hardcoded fallback.
export interface NodeSettingsFieldBounds {
	readonly llamaMaxLoadedProcesses: NumericBounds;
	readonly llamaIdleTimeToLiveSeconds: NumericBounds;
	readonly maxResponseSizeMb: NumericBounds;
	readonly orchestrationIdleTimeoutSeconds: NumericBounds;
	readonly agentHomeTimeoutSeconds: NumericBounds;
	readonly maxPendingToolCallAgeMinutes: NumericBounds;
}

export function toNodeSettingsFieldBounds(response: NodeSettingsResponse | undefined): NodeSettingsFieldBounds {
	return {
		llamaMaxLoadedProcesses: boundsOf(
			response?.minLlamaMaxLoadedProcesses,
			response?.maxAllowedLlamaMaxLoadedProcesses,
			nodeSettingsFieldBounds.llamaMaxLoadedProcesses,
		),
		llamaIdleTimeToLiveSeconds: boundsOf(
			response?.minLlamaIdleTimeToLiveSeconds,
			response?.maxAllowedLlamaIdleTimeToLiveSeconds,
			nodeSettingsFieldBounds.llamaIdleTimeToLiveSeconds,
		),
		maxResponseSizeMb: boundsOf(
			response?.minMaxResponseSizeMb,
			response?.maxAllowedMaxResponseSizeMb,
			nodeSettingsFieldBounds.maxResponseSizeMb,
		),
		orchestrationIdleTimeoutSeconds: boundsOf(
			response?.minOrchestrationIdleTimeoutSeconds,
			response?.maxAllowedOrchestrationIdleTimeoutSeconds,
			nodeSettingsFieldBounds.orchestrationIdleTimeoutSeconds,
		),
		agentHomeTimeoutSeconds: boundsOf(
			response?.minAgentHomeTimeoutSeconds,
			response?.maxAllowedAgentHomeTimeoutSeconds,
			nodeSettingsFieldBounds.agentHomeTimeoutSeconds,
		),
		maxPendingToolCallAgeMinutes: boundsOf(
			response?.minMaxPendingToolCallAgeMinutes,
			response?.maxAllowedMaxPendingToolCallAgeMinutes,
			nodeSettingsFieldBounds.maxPendingToolCallAgeMinutes,
		),
	};
}

// The outcome of validating the whole form: the request body containing ONLY changed fields, plus a per-field error
// map. When `errors` is non-empty the caller must not save.
export interface NodeSettingsValidationResult {
	readonly body: SaveNodeSettingsRequest;
	readonly errors: Readonly<Record<string, string>>;
}

// A positive-long validator for the AgentHome byte caps (> 0).
function toValidPositiveLong(value: number | string): number | undefined {
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isInteger(numeric) || numeric <= 0) {
		return undefined;
	}
	return numeric;
}

// Builds the PUT body from the edited form, including ONLY fields that differ from the loaded baseline, and collects
// per-field validation errors. Developer-only fields are validated + included only when `includeDeveloperFields` is
// true (they are not rendered, so an off-mode save must never touch them). Error values are i18n suffix keys the page
// maps to messages.
export function buildNodeSettingsRequest(
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	bounds: NodeSettingsFieldBounds,
	includeDeveloperFields: boolean,
): NodeSettingsValidationResult {
	const body: SaveNodeSettingsRequest = {};
	const errors: Record<string, string> = {};

	// defaultModelName — free text; empty string clears it (sent as empty string -> backend treats as unset/seed).
	if (form.defaultModelName !== baseline.defaultModelName) {
		body.defaultModelName = form.defaultModelName.trim().length > 0 ? form.defaultModelName.trim() : null;
	}

	if (form.enableTools !== baseline.enableTools) {
		body.enableTools = form.enableTools;
	}

	// toolCapableModels — list editor; cleaned + validated. Compared by JSON for a stable change check.
	const toolModels = validateToolCapableModels(form.toolCapableModels);
	if (toolModels.hasInvalid) {
		errors["toolCapableModels"] = "invalid";
	} else if (JSON.stringify(toolModels.value) !== JSON.stringify(baseline.toolCapableModels)) {
		body.toolCapableModels = toolModels.value;
	}

	if (form.ollamaEndpoint !== baseline.ollamaEndpoint) {
		const endpoint = validateOllamaEndpoint(form.ollamaEndpoint);
		if (endpoint.error) {
			errors["ollamaEndpoint"] = endpoint.error;
		} else {
			body.ollamaEndpoint = endpoint.value ?? null;
		}
	}

	if (form.huggingFaceDefaultQuant !== baseline.huggingFaceDefaultQuant) {
		body.huggingFaceDefaultQuant =
			form.huggingFaceDefaultQuant.trim().length > 0 ? form.huggingFaceDefaultQuant.trim() : null;
	}

	if (form.recommendedLlamaCppTag !== baseline.recommendedLlamaCppTag) {
		const tag = validateLlamaCppTag(form.recommendedLlamaCppTag);
		if (tag.error) {
			errors["recommendedLlamaCppTag"] = tag.error;
		} else {
			body.recommendedLlamaCppTag = tag.value ?? null;
		}
	}

	collectBoundedInt(
		form.llamaMaxLoadedProcesses,
		baseline.llamaMaxLoadedProcesses,
		bounds.llamaMaxLoadedProcesses,
		"llamaMaxLoadedProcesses",
		body,
		errors,
		(v) => {
			body.llamaMaxLoadedProcesses = v;
		},
	);
	collectBoundedInt(
		form.llamaIdleTimeToLiveSeconds,
		baseline.llamaIdleTimeToLiveSeconds,
		bounds.llamaIdleTimeToLiveSeconds,
		"llamaIdleTimeToLiveSeconds",
		body,
		errors,
		(v) => {
			body.llamaIdleTimeToLiveSeconds = v;
		},
	);
	collectBoundedInt(
		form.maxResponseSizeMb,
		baseline.maxResponseSizeMb,
		bounds.maxResponseSizeMb,
		"maxResponseSizeMb",
		body,
		errors,
		(v) => {
			body.maxResponseSizeMb = v;
		},
	);

	if (includeDeveloperFields) {
		collectBoundedInt(
			form.orchestrationIdleTimeoutSeconds,
			baseline.orchestrationIdleTimeoutSeconds,
			bounds.orchestrationIdleTimeoutSeconds,
			"orchestrationIdleTimeoutSeconds",
			body,
			errors,
			(v) => {
				body.orchestrationIdleTimeoutSeconds = v;
			},
		);
		collectBoundedInt(
			form.agentHomePrepareTimeoutSeconds,
			baseline.agentHomePrepareTimeoutSeconds,
			bounds.agentHomeTimeoutSeconds,
			"agentHomePrepareTimeoutSeconds",
			body,
			errors,
			(v) => {
				body.agentHomePrepareTimeoutSeconds = v;
			},
		);
		collectBoundedInt(
			form.agentHomeCommandTimeoutSeconds,
			baseline.agentHomeCommandTimeoutSeconds,
			bounds.agentHomeTimeoutSeconds,
			"agentHomeCommandTimeoutSeconds",
			body,
			errors,
			(v) => {
				body.agentHomeCommandTimeoutSeconds = v;
			},
		);
		collectBoundedInt(
			form.maxPendingToolCallAgeMinutes,
			baseline.maxPendingToolCallAgeMinutes,
			bounds.maxPendingToolCallAgeMinutes,
			"maxPendingToolCallAgeMinutes",
			body,
			errors,
			(v) => {
				body.maxPendingToolCallAgeMinutes = v;
			},
		);
		collectPositiveLong(
			form.agentHomeMaxSelectedFolderBytes,
			baseline.agentHomeMaxSelectedFolderBytes,
			"agentHomeMaxSelectedFolderBytes",
			errors,
			(v) => {
				body.agentHomeMaxSelectedFolderBytes = v;
			},
		);
		collectPositiveLong(form.agentHomeMaxPatchBytes, baseline.agentHomeMaxPatchBytes, "agentHomeMaxPatchBytes", errors, (v) => {
			body.agentHomeMaxPatchBytes = v;
		});
	}

	return { body, errors };
}

// Validates one bounded-int field against the baseline; on change either records an error or applies the parsed value
// via `apply`. `body`/`errors` are mutated in place (keeps the per-field call sites flat).
function collectBoundedInt(
	value: number | string,
	baseline: number | string,
	bounds: NumericBounds,
	field: string,
	_body: SaveNodeSettingsRequest,
	errors: Record<string, string>,
	apply: (parsed: number) => void,
): void {
	if (value === baseline) {
		return;
	}
	const parsed = toValidBoundedInt(value, bounds);
	if (parsed === undefined) {
		errors[field] = "range";
		return;
	}
	apply(parsed);
}

function collectPositiveLong(
	value: number | string,
	baseline: number | string,
	field: string,
	errors: Record<string, string>,
	apply: (parsed: number) => void,
): void {
	if (value === baseline) {
		return;
	}
	const parsed = toValidPositiveLong(value);
	if (parsed === undefined) {
		errors[field] = "positive";
		return;
	}
	apply(parsed);
}
