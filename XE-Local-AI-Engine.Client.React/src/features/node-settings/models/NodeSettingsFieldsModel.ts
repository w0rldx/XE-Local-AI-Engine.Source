import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse,
	XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest,
} from "@/core/api/generated";
import { type KvCacheType, kvCacheTypes } from "@/core/models/KvCacheTypes";

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
const nodeSettingsFieldBounds = {
	llamaMaxLoadedProcesses: { min: 1, max: 16 },
	llamaIdleTimeToLiveSeconds: { min: 30, max: 86400 },
	keepModelWarmIntervalSeconds: { min: 5, max: 3600 },
	maxResponseSizeMb: { min: 1, max: 100 },
	orchestrationIdleTimeoutSeconds: { min: 1, max: 3600 },
	agentHomeTimeoutSeconds: { min: 1, max: 86400 },
	maxPendingToolCallAgeMinutes: { min: 1, max: 60 },
	detachedGraceSeconds: { min: 0, max: 86400 },
	chatCacheReuse: { min: 0, max: 8192 },
	speculativeDraftMaxTokens: { min: 0, max: 16 },
} as const satisfies Record<string, NumericBounds>;

// Speculative-decoding modes, each mapped to its capability class — mirrors the backend SpeculativeModeClass, and the
// `draft-` name prefix is NOT the capability test: `external-draft` runs a second GGUF drafter (needs a draft model,
// uses additional VRAM), `main-model-heads` (draft-mtp) drafts from multi-token-prediction heads inside the MAIN model
// and needs no draft model at all, `draftless` (ngram-*) self-speculates from context. Kept in sync with the backend
// accepted set (pinned llama.cpp build b10201). The full set is used for validation; the settings UI offers a curated
// subset (see speculativeModeSelectValues).
export const SPECULATIVE_DISABLED_MODE = "none";

type SpeculativeModeClass = "disabled" | "draftless" | "external-draft" | "main-model-heads";

const speculativeModeClasses = new Map<string, SpeculativeModeClass>([
	[SPECULATIVE_DISABLED_MODE, "disabled"],
	["ngram-simple", "draftless"],
	["ngram-map-k", "draftless"],
	["ngram-map-k4v", "draftless"],
	["ngram-mod", "draftless"],
	["ngram-cache", "draftless"],
	["draft-simple", "external-draft"],
	["draft-eagle3", "external-draft"],
	["draft-dflash", "external-draft"],
	["draft-dspark", "external-draft"],
	["draft-mtp", "main-model-heads"],
]);

// The modes surfaced in the settings Select (curated for operators), in display order. `none` renders as "Off".
export const speculativeModeSelectValues = [
	SPECULATIVE_DISABLED_MODE,
	"ngram-mod",
	"ngram-cache",
	"draft-simple",
	"draft-eagle3",
	"draft-dflash",
	"draft-dspark",
	"draft-mtp",
] as const;

// The provider default for a GPU chat spawn; the node setting seeds the launch policy with it when unset.
export const KV_CACHE_TYPE_DEFAULT: KvCacheType = "q8_0";

// The shared allow-list is the only one — see core/models/KvCacheTypes.
export const kvCacheTypeSelectValues = kvCacheTypes;

function isAllowedKvCacheType(type: string): boolean {
	return (kvCacheTypeSelectValues as readonly string[]).includes(type.trim());
}

export function isAllowedSpeculativeMode(mode: string): boolean {
	return speculativeModeClasses.has(mode.trim());
}

// True only for modes that load a SECOND GGUF as the drafter, so the form must require a draft model for them.
// draft-mtp is deliberately false: its drafter lives in the main model.
export function requiresExternalDraftModel(mode: string): boolean {
	return speculativeModeClasses.get(mode.trim()) === "external-draft";
}

// `--spec-draft-n-max` (draft tokens per step) is honoured by both draft classes, including draft-mtp; ngram-* modes
// size their drafts from their own knobs instead.
export function usesDraftTokensPerStep(mode: string): boolean {
	const modeClass = speculativeModeClasses.get(mode.trim());
	return modeClass === "external-draft" || modeClass === "main-model-heads";
}

// Recommended llama.cpp tag must match the upstream release-tag scheme `b<N>` (e.g. b9692). Enforced at every entry
// point (settings save, update endpoint, catalog, manager) to prevent path/URL injection into the download URL.
const llamaCppTagPattern = /^b\d+$/;

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

export { newUsageRateRow, toUsageRateRows, validateUsageRates } from "@/features/node-settings/models/NodeSettingsUsageRateModel";
import {
	canonicalRateMap,
	toUsageRateRows,
	validateUsageRates,
} from "@/features/node-settings/models/NodeSettingsUsageRateModel";
import type { UsageRateRow } from "@/features/node-settings/models/NodeSettingsUsageRateModel";
export type { UsageRateRow } from "@/features/node-settings/models/NodeSettingsUsageRateModel";

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
	customToolsEnabled: boolean;
	toolCapableModels: string[];
	ollamaEndpoint: string;
	huggingFaceDefaultQuant: string;
	recommendedLlamaCppTag: string;
	llamaMaxLoadedProcesses: number | string;
	llamaIdleTimeToLiveSeconds: number | string;
	keepModelWarmEnabled: boolean;
	keepModelWarmModelName: string;
	keepModelWarmIntervalSeconds: number | string;
	maxResponseSizeMb: number | string;
	// Chat launch tuning (KV-cache type + speculative decoding + prompt-cache reuse)
	kvCacheType: string;
	speculativeMode: string;
	speculativeDraftModelName: string;
	speculativeDraftMaxTokens: number | string;
	chatCacheReuse: number | string;
	// Knowledge-base reranker (empty = reranking off)
	rerankerModelName: string;
	// Node-local model a FAST `auto` reasoning-effort turn may be moved onto (empty = no swap, ladder only)
	autoEffortFastModelName: string;
	// Per-model usage cost rates (USD per 1M tokens), edited as ordered rows and reduced to the stored map on save.
	usageRates: UsageRateRow[];
	// Developer-only
	orchestrationIdleTimeoutSeconds: number | string;
	agentHomePrepareTimeoutSeconds: number | string;
	agentHomeCommandTimeoutSeconds: number | string;
	agentHomeMaxSelectedFolderBytes: number | string;
	agentHomeMaxPatchBytes: number | string;
	maxPendingToolCallAgeMinutes: number | string;
	detachedGraceSeconds: number | string;
}

// Defaults used when the response omits a field. These mirror the backend seed defaults (the `Default*` consts in
// StoredNodeSettings.cs) so the form renders sensible values on an old server that has not yet persisted the field.
export const nodeSettingsFieldDefaults: NodeSettingsFieldsForm = {
	defaultModelName: "",
	enableTools: true,
	customToolsEnabled: false,
	toolCapableModels: [],
	ollamaEndpoint: "",
	huggingFaceDefaultQuant: "",
	recommendedLlamaCppTag: "",
	llamaMaxLoadedProcesses: 3,
	llamaIdleTimeToLiveSeconds: 900,
	keepModelWarmEnabled: false,
	keepModelWarmModelName: "",
	keepModelWarmIntervalSeconds: 300,
	maxResponseSizeMb: 10,
	kvCacheType: KV_CACHE_TYPE_DEFAULT,
	speculativeMode: SPECULATIVE_DISABLED_MODE,
	speculativeDraftModelName: "",
	speculativeDraftMaxTokens: 3,
	chatCacheReuse: 256,
	rerankerModelName: "",
	autoEffortFastModelName: "",
	usageRates: [],
	orchestrationIdleTimeoutSeconds: 120,
	agentHomePrepareTimeoutSeconds: 900,
	agentHomeCommandTimeoutSeconds: 300,
	agentHomeMaxSelectedFolderBytes: 536870912,
	agentHomeMaxPatchBytes: 52428800,
	maxPendingToolCallAgeMinutes: 10,
	detachedGraceSeconds: 300,
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
		customToolsEnabled: response.customToolsEnabled ?? nodeSettingsFieldDefaults.customToolsEnabled,
		toolCapableModels: response.toolCapableModels ? [...response.toolCapableModels] : [],
		ollamaEndpoint: response.ollamaEndpoint ?? "",
		huggingFaceDefaultQuant: response.huggingFaceDefaultQuant ?? "",
		recommendedLlamaCppTag: response.recommendedLlamaCppTag ?? "",
		llamaMaxLoadedProcesses: numberOr(response.llamaMaxLoadedProcesses, nodeSettingsFieldDefaults.llamaMaxLoadedProcesses),
		llamaIdleTimeToLiveSeconds: numberOr(
			response.llamaIdleTimeToLiveSeconds,
			nodeSettingsFieldDefaults.llamaIdleTimeToLiveSeconds,
		),
		keepModelWarmEnabled: response.keepModelWarmEnabled ?? nodeSettingsFieldDefaults.keepModelWarmEnabled,
		keepModelWarmModelName: response.keepModelWarmModelName ?? "",
		keepModelWarmIntervalSeconds: numberOr(
			response.keepModelWarmIntervalSeconds,
			nodeSettingsFieldDefaults.keepModelWarmIntervalSeconds,
		),
		maxResponseSizeMb: numberOr(response.maxResponseSizeMb, nodeSettingsFieldDefaults.maxResponseSizeMb),
		kvCacheType: response.kvCacheType ?? nodeSettingsFieldDefaults.kvCacheType,
		speculativeMode: response.speculativeMode ?? nodeSettingsFieldDefaults.speculativeMode,
		speculativeDraftModelName: response.speculativeDraftModelName ?? "",
		speculativeDraftMaxTokens: numberOr(response.speculativeDraftMaxTokens, nodeSettingsFieldDefaults.speculativeDraftMaxTokens),
		chatCacheReuse: numberOr(response.chatCacheReuse, nodeSettingsFieldDefaults.chatCacheReuse),
		rerankerModelName: response.rerankerModelName ?? "",
		autoEffortFastModelName: response.autoEffortFastModelName ?? "",
		usageRates: toUsageRateRows(response.usageRates),
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
		detachedGraceSeconds: numberOr(response.detachedGraceSeconds, nodeSettingsFieldDefaults.detachedGraceSeconds),
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
	readonly keepModelWarmIntervalSeconds: NumericBounds;
	readonly maxResponseSizeMb: NumericBounds;
	readonly chatCacheReuse: NumericBounds;
	readonly speculativeDraftMaxTokens: NumericBounds;
	readonly orchestrationIdleTimeoutSeconds: NumericBounds;
	readonly agentHomeTimeoutSeconds: NumericBounds;
	readonly maxPendingToolCallAgeMinutes: NumericBounds;
	readonly detachedGraceSeconds: NumericBounds;
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
		keepModelWarmIntervalSeconds: boundsOf(
			response?.minKeepModelWarmIntervalSeconds,
			response?.maxAllowedKeepModelWarmIntervalSeconds,
			nodeSettingsFieldBounds.keepModelWarmIntervalSeconds,
		),
		maxResponseSizeMb: boundsOf(
			response?.minMaxResponseSizeMb,
			response?.maxAllowedMaxResponseSizeMb,
			nodeSettingsFieldBounds.maxResponseSizeMb,
		),
		chatCacheReuse: boundsOf(
			response?.minChatCacheReuse,
			response?.maxAllowedChatCacheReuse,
			nodeSettingsFieldBounds.chatCacheReuse,
		),
		speculativeDraftMaxTokens: boundsOf(
			response?.minSpeculativeDraftMaxTokens,
			response?.maxAllowedSpeculativeDraftMaxTokens,
			nodeSettingsFieldBounds.speculativeDraftMaxTokens,
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
		detachedGraceSeconds: boundsOf(
			response?.minDetachedGraceSeconds,
			response?.maxAllowedDetachedGraceSeconds,
			nodeSettingsFieldBounds.detachedGraceSeconds,
		),
	};
}

// The fields whose runtime consumer reads them exactly ONCE — at DI composition / singleton construction — so a Save
// persists immediately but the running node keeps its old value until it restarts. This is the single source of truth
// for both the per-field hint in NodeSettingsFieldsCard and the post-save "restart required" notice; verify the named
// backend consumer before adding or removing an entry (every one below confirmed against the composition root):
//   defaultModelName                — AddNodeModelRuntimeExtensions.ResolveChatConnectionSettings (GetDefaultModelName)
//                                     + InvocationRunner ctor. PARTIAL: the scheduler (RunSavedAgentHandler) reads live.
//   ollamaEndpoint                  — AddNodeModelRuntimeExtensions.ResolveChatConnectionSettings (GetOllamaEndpoint)
//   huggingFaceDefaultQuant         — AddNodeModelRuntimeExtensions.BuildSeededHuggingFaceOptions
//   llamaMaxLoadedProcesses         — AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions
//   llamaIdleTimeToLiveSeconds      — same seed method
//   chatCacheReuse                  — same seed method (LlamaServerSupervisorOptions.ChatCacheReuse)
//   kvCacheType                     — AddNodeModelRuntimeExtensions.BuildSeededLlamaServerLaunchPolicyOptions, the
//                                     ONLY consumer (LlamaServerLaunchPolicyOptions is built once at host build)
//   speculativeMode                 — same seed method
//   speculativeDraftModelName       — same seed method
//   speculativeDraftMaxTokens       — same seed method
//   rerankerModelName               — AddNodeKnowledgeBaseExtensions PostConfigure<KnowledgeBaseOptions>
//   maxResponseSizeMb               — InvocationRunner ctor (GetMaxResponseSizeMb)
//   orchestrationIdleTimeoutSeconds — AddNodeModelRuntimeExtensions Configure<OrchestrationAgentOptions>
//   maxPendingToolCallAgeMinutes    — InvocationRunner ctor. PARTIAL: ToolCallCleanupService sweeps with a live read,
//                                     but a running invocation's own approval-wait timer was fixed at process start.
// Every other form field is read live on each call and must NOT be listed here: agentHome*, keepModelWarm*,
// toolCapableModels, enableTools, customToolsEnabled, detachedGraceSeconds, usageRates, recommendedLlamaCppTag, and the
// message-request timeout.
export const restartGatedNodeSettingsFields: ReadonlySet<keyof NodeSettingsFieldsForm> = new Set<keyof NodeSettingsFieldsForm>([
	"defaultModelName",
	"ollamaEndpoint",
	"huggingFaceDefaultQuant",
	"llamaMaxLoadedProcesses",
	"llamaIdleTimeToLiveSeconds",
	"chatCacheReuse",
	"kvCacheType",
	"speculativeMode",
	"speculativeDraftModelName",
	"speculativeDraftMaxTokens",
	"rerankerModelName",
	"maxResponseSizeMb",
	"orchestrationIdleTimeoutSeconds",
	"maxPendingToolCallAgeMinutes",
]);

// True when a built save body carries at least one restart-gated field, so the page can tell the operator a restart is
// needed. The request keys mirror the form keys 1:1, and the body only ever holds CHANGED fields.
export function touchesRestartGatedField(body: SaveNodeSettingsRequest): boolean {
	return Object.keys(body).some((key) => restartGatedNodeSettingsFields.has(key as keyof NodeSettingsFieldsForm));
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

	if (form.customToolsEnabled !== baseline.customToolsEnabled) {
		body.customToolsEnabled = form.customToolsEnabled;
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
		body.huggingFaceDefaultQuant = form.huggingFaceDefaultQuant.trim().length > 0 ? form.huggingFaceDefaultQuant.trim() : null;
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

	if (form.keepModelWarmEnabled !== baseline.keepModelWarmEnabled) {
		// Explicit false is meaningful: omission preserves the stored value, while false turns the live service off.
		body.keepModelWarmEnabled = form.keepModelWarmEnabled;
	}

	const keepWarmModelName = form.keepModelWarmModelName.trim();
	if (form.keepModelWarmEnabled && keepWarmModelName.length === 0) {
		errors["keepModelWarmModelName"] = "requiredKeepWarmModel";
	} else if (form.keepModelWarmModelName !== baseline.keepModelWarmModelName) {
		// The PUT is null-preserving (null/omitted = keep current), so an empty string is the explicit clear signal.
		body.keepModelWarmModelName = keepWarmModelName;
	}

	if (form.keepModelWarmEnabled) {
		const maxLoadedProcesses = toValidBoundedInt(form.llamaMaxLoadedProcesses, bounds.llamaMaxLoadedProcesses);
		if (maxLoadedProcesses !== undefined && maxLoadedProcesses < 2) {
			errors["llamaMaxLoadedProcesses"] = "keepWarmCapacity";
		}

		// A disabled feature must always be saveable, even if the operator entered an invalid interval before turning it
		// off. The disabled interval control cannot be corrected in that state, and omission preserves the last valid value.
		collectBoundedInt(
			form.keepModelWarmIntervalSeconds,
			baseline.keepModelWarmIntervalSeconds,
			bounds.keepModelWarmIntervalSeconds,
			"keepModelWarmIntervalSeconds",
			body,
			errors,
			(v) => {
				body.keepModelWarmIntervalSeconds = v;
			},
		);

		const warmInterval = toValidBoundedInt(form.keepModelWarmIntervalSeconds, bounds.keepModelWarmIntervalSeconds);
		const idleTtl = toValidBoundedInt(form.llamaIdleTimeToLiveSeconds, bounds.llamaIdleTimeToLiveSeconds);
		if (warmInterval !== undefined && idleTtl !== undefined && warmInterval >= idleTtl) {
			errors["keepModelWarmIntervalSeconds"] = "belowIdleTtl";
		}
	}
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

	// KV-cache type. Sent whenever it differs from the baseline; an unknown value is a hard error rather than a silent
	// drop, the same way speculativeMode is handled. Changing this invalidates every frozen inference profile on the
	// node, which the field description spells out.
	if (form.kvCacheType !== baseline.kvCacheType) {
		if (!isAllowedKvCacheType(form.kvCacheType)) {
			errors["kvCacheType"] = "type";
		} else {
			body.kvCacheType = form.kvCacheType.trim();
		}
	}

	// Speculative decoding mode. Sent whenever it differs from the baseline (including a switch back to "none"). An
	// unknown mode is a hard error rather than a silent drop.
	if (form.speculativeMode !== baseline.speculativeMode) {
		if (!isAllowedSpeculativeMode(form.speculativeMode)) {
			errors["speculativeMode"] = "mode";
		} else {
			body.speculativeMode = form.speculativeMode.trim();
		}
	}

	// An external-draft mode requires a draft model; guard even when the mode itself did not change (e.g. the model was
	// cleared). draft-mtp is exempt — it drafts from the main model's own heads.
	const draftModelName = form.speculativeDraftModelName.trim();
	if (requiresExternalDraftModel(form.speculativeMode) && draftModelName.length === 0) {
		errors["speculativeDraftModelName"] = "required";
	} else if (form.speculativeDraftModelName !== baseline.speculativeDraftModelName) {
		// Empty clears the stored name (sent as null); a non-empty value is the installed model name to resolve.
		body.speculativeDraftModelName = draftModelName.length > 0 ? draftModelName : null;
	}

	collectBoundedInt(
		form.speculativeDraftMaxTokens,
		baseline.speculativeDraftMaxTokens,
		bounds.speculativeDraftMaxTokens,
		"speculativeDraftMaxTokens",
		body,
		errors,
		(v) => {
			body.speculativeDraftMaxTokens = v;
		},
	);
	collectBoundedInt(form.chatCacheReuse, baseline.chatCacheReuse, bounds.chatCacheReuse, "chatCacheReuse", body, errors, (v) => {
		body.chatCacheReuse = v;
	});

	// Knowledge-base reranker model — free model name; empty string is the "Off" signal (backend Normalize maps blank to
	// null = reranking disabled). Sent whenever it differs from the baseline, including a switch back to "Off".
	if (form.rerankerModelName !== baseline.rerankerModelName) {
		body.rerankerModelName = form.rerankerModelName.trim();
	}

	// Fast model for automatic reasoning effort — same shape as the reranker: empty string is the "Off" signal the
	// backend Normalize maps to null. Deliberately NOT restart-gated: the dispatcher reads it per send, so a save
	// applies to the very next turn.
	if (form.autoEffortFastModelName !== baseline.autoEffortFastModelName) {
		body.autoEffortFastModelName = form.autoEffortFastModelName.trim();
	}

	// Usage rates — an editable per-model rate map. Validated to non-negative numbers with non-empty names; an invalid
	// row is a hard error. Sent (as the map, or null when emptied) only when it differs from the loaded baseline; the
	// backend field is null-preserving so omitting it keeps the current table.
	const rates = validateUsageRates(form.usageRates);
	if (rates.hasInvalid) {
		errors["usageRates"] = "rate";
	} else if (canonicalRateMap(rates.map) !== canonicalRateMap(validateUsageRates(baseline.usageRates).map)) {
		body.usageRates = rates.map;
	}

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
		collectBoundedInt(
			form.detachedGraceSeconds,
			baseline.detachedGraceSeconds,
			bounds.detachedGraceSeconds,
			"detachedGraceSeconds",
			body,
			errors,
			(v) => {
				body.detachedGraceSeconds = v;
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
