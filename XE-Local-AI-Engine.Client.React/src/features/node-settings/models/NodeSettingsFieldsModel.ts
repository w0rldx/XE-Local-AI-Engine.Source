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

// An installed model offered in one of the settings-form model pickers (draft, keep-warm, auto-effort fast, reranker).
// `value` is the model name; the backend resolves it to a path at save.
export interface NodeSettingsModelOption {
	readonly value: string;
	readonly label: string;
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
	speculativeDraftGpuLayers: { min: 0, max: 1000 },
	llamaChatCacheRamMiB: { min: 0, max: 131072 },
	llamaReadinessTimeoutCapSeconds: { min: 120, max: 3600 },
	llamaChatHttpTimeoutSeconds: { min: 60, max: 86400 },
	llamaEmbeddingHttpTimeoutSeconds: { min: 10, max: 3600 },
	llamaCpuThreadReserve: { min: 0, max: 64 },
	llamaGpuReservePercent: { min: 0, max: 50 },
	llamaRamReservePercent: { min: 0, max: 75 },
	imageIdleTimeToLiveSeconds: { min: 30, max: 86400 },
	modelFitSafetyMarginPercent: { min: 0, max: 50 },
	maxProviderCallsPerInvocation: { min: 10, max: 2000 },
	customToolMaxTimeoutSeconds: { min: 30, max: 3600 },
	webFetchTimeoutSeconds: { min: 5, max: 120 },
	webFetchMaxContentChars: { min: 1000, max: 100000 },
	knowledgeSearchDefaultResults: { min: 1, max: 20 },
	knowledgeSearchMaxResults: { min: 1, max: 20 },
	reasoningBudgetMinimalTokens: { min: 128, max: 131072 },
	reasoningBudgetLowTokens: { min: 128, max: 131072 },
	reasoningBudgetMediumTokens: { min: 128, max: 131072 },
	reasoningBudgetHighTokens: { min: 128, max: 131072 },
	chatOutputCapMaxTokens: { min: 256, max: 131072 },
	huggingFaceDownloadConnections: { min: 1, max: 16 },
	transcriptionIdleTimeoutMinutes: { min: 1, max: 240 },
	transcriptionInferenceTimeoutMinutes: { min: 1, max: 480 },
	agentHomeMaxRunSeconds: { min: 60, max: 86400 },
	agentHomeRunRetentionDays: { min: 1, max: 365 },
	huggingFaceDiskMarginBytes: { min: 1, max: 1024 ** 4 },
	toolPipelineMaxIterationsPerRequest: { min: 1, max: 200 },
	toolPipelineMaxToolResultChars: { min: 1024, max: 1000000 },
	toolPipelineMaxConsecutiveInvalidToolCalls: { min: 1, max: 20 },
	defaultContextTokens: { min: 1024, max: 1048576 },
	providerBudgetRecentMessagesToKeep: { min: 2, max: 100 },
	providerBudgetMaxCumulativeInputTokens: { min: 100000, max: 100000000 },
	contextBudgetRecentTurnKeepCount: { min: 2, max: 50 },
	compactionAutoCompactPercent: { min: 30, max: 95 },
	compactionRecentMessagesVerbatim: { min: 2, max: 100 },
	maxInlinedAttachmentChars: { min: 1000, max: 2000000 },
	knowledgeChatTopK: { min: 1, max: 20 },
	providerMaxRetries: { min: 0, max: 10 },
	spawnMaxConcurrent: { min: 1, max: 32 },
	spawnMaxCloud: { min: 0, max: 32 },
	spawnQueueWaitSeconds: { min: 0, max: 3600 },
	knowledgeRetrievalLatencyBudgetMs: { min: 50, max: 60000 },
	knowledgeScheduledReindexIntervalMinutes: { min: 5, max: 10080 },
	chatRetentionDays: { min: 1, max: 3650 },
	agentExecutionLogRetentionDays: { min: 1, max: 3650 },
	nodeDbBackupRetainCount: { min: 1, max: 100 },
	benchmarkKldCacheMaxBytes: { min: 1024 ** 3, max: 4 * 1024 ** 4 },
	schedulerHistoryRetentionDays: { min: 1, max: 3650 },
	imageMaxLoadedProcesses: { min: 1, max: 4 },
	graphWorkflowMaxConcurrentRuns: { min: 1, max: 64 },
	graphWorkflowDefaultNodeTimeoutSeconds: { min: 30, max: 86400 },
	workSessionMaxStepsPerRun: { min: 1, max: 1000 },
	workSessionMaxConcurrentSessions: { min: 1, max: 64 },
	developmentMaxAttemptDurationSeconds: { min: 60, max: 86400 },
	developmentMaxToolCalls: { min: 1, max: 1024 },
	developmentMaxOutputTokens: { min: 256, max: 1000000 },
	agentHomeMaxInnerToolCalls: { min: 1, max: 1000 },
	agentHomePatchApplyTimeoutSeconds: { min: 10, max: 3600 },
	agentHomeRunRetentionMaxRuns: { min: 0, max: 100000 },
	agentHomeRunRetentionMaxTotalBytes: { min: 0, max: 1024 ** 4 },
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

// The external-access profiles an operator can actually pick. The backend stores two more literals the client never
// sends: "custom", which it stamps when a save carries individual switches and no profile (display-only, rendered as a
// disabled option on the settings Select), and "pending", which the first-run route intercepts. Naming only the two
// choosable ones here makes "the client never computes custom" a compile error rather than a runtime guard.
export type ExternalAccessPreset = "recommended" | "offline";

// The provider default for a GPU chat spawn; the node setting seeds the launch policy with it when unset.
export const KV_CACHE_TYPE_DEFAULT: KvCacheType = "q8_0";

// The shared allow-list is the only one — see core/models/KvCacheTypes.
export const kvCacheTypeSelectValues = kvCacheTypes;

function isAllowedKvCacheType(type: string): boolean {
	return (kvCacheTypeSelectValues as readonly string[]).includes(type.trim());
}

function isAllowedSpeculativeMode(mode: string): boolean {
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

// Resolves a controlled numeric input (number or string) to a valid integer within [min, max], or undefined when the
// value is empty / fractional / out of range. Mirrors `toValidNodeSettingsTimeoutSeconds`.
function toValidBoundedInt(value: number | string, bounds: NumericBounds): number | undefined {
	const numeric = typeof value === "number" ? value : Number(value);
	if (!Number.isInteger(numeric) || numeric < bounds.min || numeric > bounds.max) {
		return undefined;
	}
	return numeric;
}

// http/https URL validator for the Ollama endpoint and the SearXNG URL (mirrors the server's BeAbsoluteHttpUrl). Empty
// resolves to undefined; each caller decides what a blank means on save.
const httpUrlSchema = z
	.string()
	.trim()
	.url()
	.refine((value) => /^https?:\/\//i.test(value), { message: "protocol" });

export function validateOptionalHttpUrl(value: string): { value?: string; error?: "url" } {
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

import type { UsageRateRow } from "@/features/node-settings/models/NodeSettingsUsageRateModel";
import {
	canonicalRateMap,
	toUsageRateRows,
	validateUsageRates,
} from "@/features/node-settings/models/NodeSettingsUsageRateModel";

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
	toolRelevanceEnabled: boolean;
	toolCapableModels: string[];
	// Web access (web_search + web_fetch). Off by default; an empty SearXNG URL means DuckDuckGo.
	webAccessEnabled: boolean;
	webSearchSearxngUrl: string;
	ollamaEndpoint: string;
	huggingFaceDefaultQuant: string;
	llamaMaxLoadedProcesses: number | string;
	// Shown and edited in MINUTES (see nodeSettingsDisplayScale); the key keeps the wire name so request keys stay 1:1.
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
	// External access — the profile plus the three switches it sets. The profile is a server-owned stamp (see
	// applyExternalAccessPreset / buildNodeSettingsRequest); an empty string means the node has not decided one.
	externalAccessProfile: string;
	autoCheckApplicationUpdates: boolean;
	autoCheckRuntimeUpdates: boolean;
	autoProvisionFirstRunModel: boolean;
	// Navigation mode as the server stores it ("simple" / "advanced", or "" / another literal while undecided).
	uiMode: string;
	// Node-level voice gate and the default voice for new users on this node.
	voiceFeatureEnabled: boolean;
	defaultVoiceProfile: string;
	// Developer-only. The AgentHome timeouts are edited in MINUTES and the byte caps in MB (see nodeSettingsDisplayScale).
	orchestrationIdleTimeoutSeconds: number | string;
	agentHomePrepareTimeoutSeconds: number | string;
	agentHomeCommandTimeoutSeconds: number | string;
	agentHomeMaxSelectedFolderBytes: number | string;
	agentHomeMaxPatchBytes: number | string;
	maxPendingToolCallAgeMinutes: number | string;
	detachedGraceSeconds: number | string;
	// The curated runtime tunables (see tunableFields). Plain bounded integers; the chat HTTP timeout, the image idle TTL
	// and the AgentHome run limit are edited in MINUTES, the Hugging Face disk margin in GB.
	llamaReadinessTimeoutCapSeconds: number | string;
	llamaChatHttpTimeoutSeconds: number | string;
	llamaEmbeddingHttpTimeoutSeconds: number | string;
	llamaCpuThreadReserve: number | string;
	llamaGpuReservePercent: number | string;
	llamaRamReservePercent: number | string;
	imageIdleTimeToLiveSeconds: number | string;
	modelFitSafetyMarginPercent: number | string;
	maxProviderCallsPerInvocation: number | string;
	customToolMaxTimeoutSeconds: number | string;
	webFetchTimeoutSeconds: number | string;
	webFetchMaxContentChars: number | string;
	knowledgeSearchDefaultResults: number | string;
	knowledgeSearchMaxResults: number | string;
	// llama.cpp thinking budget per reasoning effort, and whose budget a turn with no effort gets (section "chat"). These
	// four and the two output-cap fields are blank while the node uses the shipped default (see shippedDefaults); a save
	// that blanks a stored value sends the reset sentinel.
	reasoningBudgetMinimalTokens: number | string;
	reasoningBudgetLowTokens: number | string;
	reasoningBudgetMediumTokens: number | string;
	reasoningBudgetHighTokens: number | string;
	defaultReasoningEffort: string;
	// The chat output cap: its variant and the ceiling on half the launched window (section "chat").
	chatOutputCapMode: string;
	chatOutputCapMaxTokens: number | string;
	huggingFaceDownloadConnections: number | string;
	transcriptionIdleTimeoutMinutes: number | string;
	transcriptionInferenceTimeoutMinutes: number | string;
	// Prompt-cache RAM: the stored value is null (automatic), 0 (off) or a size, so the form splits it into a mode and a
	// size. Only the size key exists on the wire; a save that returns to automatic sends the -1 reset sentinel.
	llamaChatCacheRamMode: ChatCacheRamMode;
	llamaChatCacheRamMiB: number | string;
	// Blank = never set (the runtime default); the wire has no way to clear it again, so a blank edit is refused.
	speculativeDraftGpuLayers: number | string;
	huggingFaceDiskMarginBytes: number | string;
	containerRuntimeSelection: string;
	// Developer-only, like the other AgentHome limits.
	agentHomeMaxRunSeconds: number | string;
	agentHomeRunRetentionDays: number | string;
	// Chat and agent-run knobs (section "chat"). The three tool-pipeline limits apply after a restart, the rest per turn.
	toolPipelineMaxIterationsPerRequest: number | string;
	toolPipelineMaxToolResultChars: number | string;
	toolPipelineMaxConsecutiveInvalidToolCalls: number | string;
	defaultContextTokens: number | string;
	providerBudgetRecentMessagesToKeep: number | string;
	providerBudgetMaxCumulativeInputTokens: number | string;
	contextBudgetRecentTurnKeepCount: number | string;
	compactionAutoCompactPercent: number | string;
	compactionRecentMessagesVerbatim: number | string;
	maxInlinedAttachmentChars: number | string;
	knowledgeChatTopK: number | string;
	providerMaxRetries: number | string;
	spawnMaxConcurrent: number | string;
	spawnMaxCloud: number | string;
	spawnQueueWaitSeconds: number | string;
	compactionAutoEnabled: boolean;
	compactionDistillEnabled: boolean;
	providerRetryEnabled: boolean;
	// Knowledge, privacy and usage knobs. The scheduled-reindex pair applies after a restart, the rest per search, turn,
	// run or sweep. The three background models are blank for "inherit the default model".
	knowledgeAdaptiveRerankingEnabled: boolean;
	knowledgeRetrievalLatencyBudgetMs: number | string;
	knowledgeScheduledReindexEnabled: boolean;
	knowledgeScheduledReindexIntervalMinutes: number | string;
	knowledgeAgentToolsEnabled: boolean;
	allowCloudModelAccess: boolean;
	allowCloudModelUnattendedRuns: boolean;
	allowCloudModelWebTools: boolean;
	allowCloudModelMcpTools: boolean;
	allowCloudModelSubAgents: boolean;
	playbookAnalysisModelName: string;
	playbookEvalModelName: string;
	memoryExtractionModelName: string;
	chatRetentionEnabled: boolean;
	chatRetentionDays: number | string;
	agentExecutionLogRetentionEnabled: boolean;
	agentExecutionLogRetentionDays: number | string;
	nodeDbBackupRetainCount: number | string;
	// Shown and edited in GB (see nodeSettingsDisplayScale).
	benchmarkKldCacheMaxBytes: number | string;
	schedulerHistoryRetentionDays: number | string;
	// Runtime and workspace knobs. The image pair and the seven workspace limits apply after a restart; the AgentHome four
	// are developer-only and read per run, apply or sweep (0 turns a retention cap off).
	imageMaxLoadedProcesses: number | string;
	imageTextEncoderOnGpu: boolean;
	graphWorkflowMaxConcurrentRuns: number | string;
	graphWorkflowDefaultNodeTimeoutSeconds: number | string;
	workSessionMaxStepsPerRun: number | string;
	workSessionMaxConcurrentSessions: number | string;
	developmentMaxAttemptDurationSeconds: number | string;
	developmentMaxToolCalls: number | string;
	developmentMaxOutputTokens: number | string;
	agentHomeMaxInnerToolCalls: number | string;
	agentHomePatchApplyTimeoutSeconds: number | string;
	agentHomeRunRetentionMaxRuns: number | string;
	// Shown and edited in GB (see nodeSettingsDisplayScale).
	agentHomeRunRetentionMaxTotalBytes: number | string;
	// The nine feature switches (section "general", Features card). The response carries their effective value.
	developmentEnabled: boolean;
	workSessionsEnabled: boolean;
	graphWorkflowsEnabled: boolean;
	transcriptionEnabled: boolean;
	externalAppsEnabled: boolean;
	computeEnabled: boolean;
	agentHomeEnabled: boolean;
	schedulerEnabled: boolean;
	devWorkflowsEnabled: boolean;
}

// The feature switches in display order. Explicit false is meaningful (it turns the feature off).
export const featureSwitchFields = [
	"developmentEnabled",
	"workSessionsEnabled",
	"devWorkflowsEnabled",
	"graphWorkflowsEnabled",
	"agentHomeEnabled",
	"computeEnabled",
	"externalAppsEnabled",
	"transcriptionEnabled",
	"schedulerEnabled",
] as const satisfies readonly (keyof NodeSettingsFieldsForm)[];

export type ChatCacheRamMode = "auto" | "off" | "custom";

// The request-only value that resets the prompt-cache RAM to automatic (StoredNodeSettings.LlamaChatCacheRamMiBAuto).
const CHAT_CACHE_RAM_AUTO = -1;

export const containerRuntimeSelectValues = ["auto", "docker"] as const;

// The efforts with a reasoning budget, the choices for the effort an unspecified turn takes (mirrors
// StoredNodeSettings.IsValidDefaultReasoningEffort; "none" sends no budget, so it cannot be one).
export const defaultReasoningEffortSelectValues = ["minimal", "low", "medium", "high"] as const;

// The chat output-cap variants (mirrors StoredNodeSettings.IsValidChatOutputCapMode).
export const chatOutputCapModeSelectValues = ["cap", "notice", "off"] as const;

// What the node uses while one of these fields is unset (blank in the form): ReasoningBudgets.Default and the
// StoredNodeSettings output-cap defaults. Shown as the field's default; never sent.
export const shippedDefaults = {
	reasoningBudgetMinimalTokens: 1024,
	reasoningBudgetLowTokens: 2048,
	reasoningBudgetMediumTokens: 8192,
	reasoningBudgetHighTokens: 24576,
	chatOutputCapMaxTokens: 16384,
	defaultReasoningEffort: "low",
	chatOutputCapMode: "cap",
} as const;

// The request-only value that resets a token field above to its shipped default (StoredNodeSettings.TokenSettingUnset);
// a select clears with the empty string instead.
const TOKEN_SETTING_UNSET = -1;

const unsettableTokenFields: ReadonlySet<string> = new Set([
	"reasoningBudgetMinimalTokens",
	"reasoningBudgetLowTokens",
	"reasoningBudgetMediumTokens",
	"reasoningBudgetHighTokens",
	"chatOutputCapMaxTokens",
]);

// The curated tunables that are plain bounded integers, each validated against its own server bounds entry.
const tunableFields = [
	"llamaReadinessTimeoutCapSeconds",
	"llamaChatHttpTimeoutSeconds",
	"llamaEmbeddingHttpTimeoutSeconds",
	"llamaCpuThreadReserve",
	"llamaGpuReservePercent",
	"llamaRamReservePercent",
	"imageIdleTimeToLiveSeconds",
	"modelFitSafetyMarginPercent",
	"maxProviderCallsPerInvocation",
	"customToolMaxTimeoutSeconds",
	"webFetchTimeoutSeconds",
	"webFetchMaxContentChars",
	"knowledgeSearchDefaultResults",
	"knowledgeSearchMaxResults",
	"reasoningBudgetMinimalTokens",
	"reasoningBudgetLowTokens",
	"reasoningBudgetMediumTokens",
	"reasoningBudgetHighTokens",
	"chatOutputCapMaxTokens",
	"huggingFaceDownloadConnections",
	"transcriptionIdleTimeoutMinutes",
	"transcriptionInferenceTimeoutMinutes",
	"toolPipelineMaxIterationsPerRequest",
	"toolPipelineMaxToolResultChars",
	"toolPipelineMaxConsecutiveInvalidToolCalls",
	"defaultContextTokens",
	"providerBudgetRecentMessagesToKeep",
	"providerBudgetMaxCumulativeInputTokens",
	"contextBudgetRecentTurnKeepCount",
	"compactionAutoCompactPercent",
	"compactionRecentMessagesVerbatim",
	"maxInlinedAttachmentChars",
	"knowledgeChatTopK",
	"providerMaxRetries",
	"spawnMaxConcurrent",
	"spawnMaxCloud",
	"spawnQueueWaitSeconds",
	"knowledgeRetrievalLatencyBudgetMs",
	"knowledgeScheduledReindexIntervalMinutes",
	"chatRetentionDays",
	"agentExecutionLogRetentionDays",
	"nodeDbBackupRetainCount",
	"benchmarkKldCacheMaxBytes",
	"schedulerHistoryRetentionDays",
	"imageMaxLoadedProcesses",
	"graphWorkflowMaxConcurrentRuns",
	"graphWorkflowDefaultNodeTimeoutSeconds",
	"workSessionMaxStepsPerRun",
	"workSessionMaxConcurrentSessions",
	"developmentMaxAttemptDurationSeconds",
	"developmentMaxToolCalls",
	"developmentMaxOutputTokens",
] as const;

type TunableField = (typeof tunableFields)[number];

const SECONDS_PER_MINUTE = 60;
const BYTES_PER_MB = 1024 * 1024;
const BYTES_PER_GB = 1024 * BYTES_PER_MB;

// Fields whose wire unit is too fine to read comfortably: the form holds `wire / scale` (minutes instead of seconds, MB
// instead of bytes) and buildNodeSettingsRequest multiplies back, rounding to a whole wire unit. The wire never changes.
export const nodeSettingsDisplayScale = {
	llamaIdleTimeToLiveSeconds: SECONDS_PER_MINUTE,
	agentHomePrepareTimeoutSeconds: SECONDS_PER_MINUTE,
	agentHomeCommandTimeoutSeconds: SECONDS_PER_MINUTE,
	agentHomeMaxSelectedFolderBytes: BYTES_PER_MB,
	agentHomeMaxPatchBytes: BYTES_PER_MB,
	llamaChatHttpTimeoutSeconds: SECONDS_PER_MINUTE,
	imageIdleTimeToLiveSeconds: SECONDS_PER_MINUTE,
	agentHomeMaxRunSeconds: SECONDS_PER_MINUTE,
	huggingFaceDiskMarginBytes: BYTES_PER_GB,
	benchmarkKldCacheMaxBytes: BYTES_PER_GB,
	agentHomeRunRetentionMaxTotalBytes: BYTES_PER_GB,
} as const satisfies Partial<Record<keyof NodeSettingsFieldsForm, number>>;

// The display scale of any field; 1 for a field shown in its wire unit.
export function nodeSettingsScaleOf(field: keyof NodeSettingsFieldsForm): number {
	return (nodeSettingsDisplayScale as Partial<Record<keyof NodeSettingsFieldsForm, number>>)[field] ?? 1;
}

// Wire bounds expressed in the display unit, for a field's range text and its NumberInput min/max.
export function toDisplayBounds(bounds: NumericBounds, scale: number): NumericBounds {
	return { min: bounds.min / scale, max: bounds.max / scale };
}

// A display value converted back to whole wire units. Blank or non-numeric input stays as it is, so validation still
// reports it instead of a blank silently becoming 0.
function toWireValue(value: number | string, scale: number): number | string {
	if (scale === 1 || (typeof value === "string" && value.trim().length === 0)) {
		return value;
	}
	const numeric = typeof value === "number" ? value : Number(value);
	return Number.isFinite(numeric) ? Math.round(numeric * scale) : value;
}

// Defaults used when the response omits a field. These mirror the backend seed defaults (the `Default*` consts in
// StoredNodeSettings.cs) so the form renders sensible values on an old server that has not yet persisted the field.
// Scaled fields are given in their display unit.
export const nodeSettingsFieldDefaults: NodeSettingsFieldsForm = {
	defaultModelName: "",
	enableTools: true,
	customToolsEnabled: false,
	toolRelevanceEnabled: false,
	toolCapableModels: [],
	webAccessEnabled: false,
	webSearchSearxngUrl: "",
	ollamaEndpoint: "",
	huggingFaceDefaultQuant: "",
	llamaMaxLoadedProcesses: 3,
	llamaIdleTimeToLiveSeconds: 15,
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
	// No profile until the node has one: "" is what an absent server value seeds too (see toNodeSettingsFieldsForm),
	// so the Select renders its "Not chosen" placeholder rather than claiming a decision the node never made.
	externalAccessProfile: "",
	autoCheckApplicationUpdates: true,
	autoCheckRuntimeUpdates: true,
	autoProvisionFirstRunModel: true,
	uiMode: "",
	voiceFeatureEnabled: false,
	defaultVoiceProfile: "",
	orchestrationIdleTimeoutSeconds: 120,
	agentHomePrepareTimeoutSeconds: 15,
	agentHomeCommandTimeoutSeconds: 5,
	agentHomeMaxSelectedFolderBytes: 512,
	agentHomeMaxPatchBytes: 50,
	maxPendingToolCallAgeMinutes: 10,
	detachedGraceSeconds: 300,
	llamaReadinessTimeoutCapSeconds: 600,
	llamaChatHttpTimeoutSeconds: 60,
	llamaEmbeddingHttpTimeoutSeconds: 600,
	llamaCpuThreadReserve: 1,
	llamaGpuReservePercent: 5,
	llamaRamReservePercent: 15,
	imageIdleTimeToLiveSeconds: 15,
	modelFitSafetyMarginPercent: 12,
	maxProviderCallsPerInvocation: 200,
	customToolMaxTimeoutSeconds: 300,
	webFetchTimeoutSeconds: 20,
	webFetchMaxContentChars: 12000,
	knowledgeSearchDefaultResults: 5,
	knowledgeSearchMaxResults: 20,
	// Blank = the node's shipped default (shippedDefaults).
	reasoningBudgetMinimalTokens: "",
	reasoningBudgetLowTokens: "",
	reasoningBudgetMediumTokens: "",
	reasoningBudgetHighTokens: "",
	defaultReasoningEffort: "",
	chatOutputCapMode: "",
	chatOutputCapMaxTokens: "",
	huggingFaceDownloadConnections: 4,
	transcriptionIdleTimeoutMinutes: 15,
	transcriptionInferenceTimeoutMinutes: 30,
	llamaChatCacheRamMode: "auto",
	llamaChatCacheRamMiB: "",
	speculativeDraftGpuLayers: "",
	huggingFaceDiskMarginBytes: 1,
	containerRuntimeSelection: "auto",
	agentHomeMaxRunSeconds: 10,
	agentHomeRunRetentionDays: 30,
	toolPipelineMaxIterationsPerRequest: 40,
	toolPipelineMaxToolResultChars: 65536,
	toolPipelineMaxConsecutiveInvalidToolCalls: 3,
	defaultContextTokens: 8192,
	providerBudgetRecentMessagesToKeep: 6,
	providerBudgetMaxCumulativeInputTokens: 4000000,
	contextBudgetRecentTurnKeepCount: 4,
	compactionAutoCompactPercent: 75,
	compactionRecentMessagesVerbatim: 8,
	maxInlinedAttachmentChars: 48000,
	knowledgeChatTopK: 5,
	providerMaxRetries: 2,
	spawnMaxConcurrent: 3,
	spawnMaxCloud: 3,
	spawnQueueWaitSeconds: 120,
	compactionAutoEnabled: true,
	compactionDistillEnabled: true,
	providerRetryEnabled: true,
	knowledgeAdaptiveRerankingEnabled: true,
	knowledgeRetrievalLatencyBudgetMs: 500,
	knowledgeScheduledReindexEnabled: true,
	knowledgeScheduledReindexIntervalMinutes: 60,
	knowledgeAgentToolsEnabled: true,
	allowCloudModelAccess: false,
	allowCloudModelUnattendedRuns: false,
	allowCloudModelWebTools: false,
	allowCloudModelMcpTools: false,
	allowCloudModelSubAgents: false,
	playbookAnalysisModelName: "",
	playbookEvalModelName: "",
	memoryExtractionModelName: "",
	chatRetentionEnabled: false,
	chatRetentionDays: 30,
	agentExecutionLogRetentionEnabled: true,
	agentExecutionLogRetentionDays: 30,
	nodeDbBackupRetainCount: 3,
	benchmarkKldCacheMaxBytes: 64,
	schedulerHistoryRetentionDays: 30,
	imageMaxLoadedProcesses: 1,
	imageTextEncoderOnGpu: false,
	graphWorkflowMaxConcurrentRuns: 4,
	graphWorkflowDefaultNodeTimeoutSeconds: 600,
	workSessionMaxStepsPerRun: 25,
	workSessionMaxConcurrentSessions: 1,
	developmentMaxAttemptDurationSeconds: 1800,
	developmentMaxToolCalls: 64,
	developmentMaxOutputTokens: 32768,
	agentHomeMaxInnerToolCalls: 24,
	agentHomePatchApplyTimeoutSeconds: 120,
	agentHomeRunRetentionMaxRuns: 200,
	agentHomeRunRetentionMaxTotalBytes: 2,
	// The C# code defaults of the BoolSeed calls in NodeRuntimeSettings (the response carries the effective value).
	developmentEnabled: true,
	workSessionsEnabled: false,
	graphWorkflowsEnabled: true,
	transcriptionEnabled: true,
	externalAppsEnabled: false,
	computeEnabled: false,
	agentHomeEnabled: false,
	schedulerEnabled: true,
	devWorkflowsEnabled: false,
};

// Coalesces a nullable numeric response field into a form value, falling back to the provided default when absent.
function numberOr(value: number | null | undefined, fallback: number | string): number | string {
	return value ?? fallback;
}

// The same for a scaled field: a server value is converted into the display unit, the default already is in it.
function scaledOr(value: number | null | undefined, field: keyof NodeSettingsFieldsForm): number | string {
	return value === null || value === undefined
		? (nodeSettingsFieldDefaults[field] as number | string)
		: value / nodeSettingsScaleOf(field);
}

function toChatCacheRam(
	value: number | null | undefined,
): Pick<NodeSettingsFieldsForm, "llamaChatCacheRamMode" | "llamaChatCacheRamMiB"> {
	if (value === null || value === undefined) {
		return { llamaChatCacheRamMode: "auto", llamaChatCacheRamMiB: "" };
	}
	return value === 0
		? { llamaChatCacheRamMode: "off", llamaChatCacheRamMiB: "" }
		: { llamaChatCacheRamMode: "custom", llamaChatCacheRamMiB: value };
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
		toolRelevanceEnabled: response.toolRelevanceEnabled ?? nodeSettingsFieldDefaults.toolRelevanceEnabled,
		toolCapableModels: response.toolCapableModels ? [...response.toolCapableModels] : [],
		webAccessEnabled: response.webAccessEnabled ?? nodeSettingsFieldDefaults.webAccessEnabled,
		webSearchSearxngUrl: response.webSearchSearxngUrl ?? "",
		ollamaEndpoint: response.ollamaEndpoint ?? "",
		huggingFaceDefaultQuant: response.huggingFaceDefaultQuant ?? "",
		llamaMaxLoadedProcesses: numberOr(response.llamaMaxLoadedProcesses, nodeSettingsFieldDefaults.llamaMaxLoadedProcesses),
		llamaIdleTimeToLiveSeconds: scaledOr(response.llamaIdleTimeToLiveSeconds, "llamaIdleTimeToLiveSeconds"),
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
		// An absent profile is genuinely undecided (a corrupted settings file reads as null), so it renders as an
		// unselected Select rather than being coerced to "recommended" — the node must not claim a choice it never made.
		externalAccessProfile: response.externalAccessProfile ?? "",
		autoCheckApplicationUpdates: response.autoCheckApplicationUpdates ?? nodeSettingsFieldDefaults.autoCheckApplicationUpdates,
		autoCheckRuntimeUpdates: response.autoCheckRuntimeUpdates ?? nodeSettingsFieldDefaults.autoCheckRuntimeUpdates,
		autoProvisionFirstRunModel: response.autoProvisionFirstRunModel ?? nodeSettingsFieldDefaults.autoProvisionFirstRunModel,
		uiMode: response.uiMode ?? "",
		voiceFeatureEnabled: response.voiceFeatureEnabled ?? nodeSettingsFieldDefaults.voiceFeatureEnabled,
		defaultVoiceProfile: response.defaultVoiceProfile ?? "",
		orchestrationIdleTimeoutSeconds: numberOr(
			response.orchestrationIdleTimeoutSeconds,
			nodeSettingsFieldDefaults.orchestrationIdleTimeoutSeconds,
		),
		agentHomePrepareTimeoutSeconds: scaledOr(response.agentHomePrepareTimeoutSeconds, "agentHomePrepareTimeoutSeconds"),
		agentHomeCommandTimeoutSeconds: scaledOr(response.agentHomeCommandTimeoutSeconds, "agentHomeCommandTimeoutSeconds"),
		agentHomeMaxSelectedFolderBytes: scaledOr(response.agentHomeMaxSelectedFolderBytes, "agentHomeMaxSelectedFolderBytes"),
		agentHomeMaxPatchBytes: scaledOr(response.agentHomeMaxPatchBytes, "agentHomeMaxPatchBytes"),
		maxPendingToolCallAgeMinutes: numberOr(
			response.maxPendingToolCallAgeMinutes,
			nodeSettingsFieldDefaults.maxPendingToolCallAgeMinutes,
		),
		detachedGraceSeconds: numberOr(response.detachedGraceSeconds, nodeSettingsFieldDefaults.detachedGraceSeconds),
		...(Object.fromEntries(tunableFields.map((field) => [field, scaledOr(response[field], field)])) as Pick<
			NodeSettingsFieldsForm,
			TunableField
		>),
		...toChatCacheRam(response.llamaChatCacheRamMiB),
		speculativeDraftGpuLayers: response.speculativeDraftGpuLayers ?? "",
		huggingFaceDiskMarginBytes: scaledOr(response.huggingFaceDiskMarginBytes, "huggingFaceDiskMarginBytes"),
		containerRuntimeSelection: response.containerRuntimeSelection ?? nodeSettingsFieldDefaults.containerRuntimeSelection,
		defaultReasoningEffort: response.defaultReasoningEffort ?? nodeSettingsFieldDefaults.defaultReasoningEffort,
		chatOutputCapMode: response.chatOutputCapMode ?? nodeSettingsFieldDefaults.chatOutputCapMode,
		agentHomeMaxRunSeconds: scaledOr(response.agentHomeMaxRunSeconds, "agentHomeMaxRunSeconds"),
		agentHomeRunRetentionDays: numberOr(response.agentHomeRunRetentionDays, nodeSettingsFieldDefaults.agentHomeRunRetentionDays),
		agentHomeMaxInnerToolCalls: numberOr(
			response.agentHomeMaxInnerToolCalls,
			nodeSettingsFieldDefaults.agentHomeMaxInnerToolCalls,
		),
		agentHomePatchApplyTimeoutSeconds: numberOr(
			response.agentHomePatchApplyTimeoutSeconds,
			nodeSettingsFieldDefaults.agentHomePatchApplyTimeoutSeconds,
		),
		agentHomeRunRetentionMaxRuns: numberOr(
			response.agentHomeRunRetentionMaxRuns,
			nodeSettingsFieldDefaults.agentHomeRunRetentionMaxRuns,
		),
		agentHomeRunRetentionMaxTotalBytes: scaledOr(
			response.agentHomeRunRetentionMaxTotalBytes,
			"agentHomeRunRetentionMaxTotalBytes",
		),
		compactionAutoEnabled: response.compactionAutoEnabled ?? nodeSettingsFieldDefaults.compactionAutoEnabled,
		compactionDistillEnabled: response.compactionDistillEnabled ?? nodeSettingsFieldDefaults.compactionDistillEnabled,
		providerRetryEnabled: response.providerRetryEnabled ?? nodeSettingsFieldDefaults.providerRetryEnabled,
		knowledgeAdaptiveRerankingEnabled:
			response.knowledgeAdaptiveRerankingEnabled ?? nodeSettingsFieldDefaults.knowledgeAdaptiveRerankingEnabled,
		knowledgeScheduledReindexEnabled:
			response.knowledgeScheduledReindexEnabled ?? nodeSettingsFieldDefaults.knowledgeScheduledReindexEnabled,
		knowledgeAgentToolsEnabled: response.knowledgeAgentToolsEnabled ?? nodeSettingsFieldDefaults.knowledgeAgentToolsEnabled,
		allowCloudModelAccess: response.allowCloudModelAccess ?? nodeSettingsFieldDefaults.allowCloudModelAccess,
		allowCloudModelUnattendedRuns:
			response.allowCloudModelUnattendedRuns ?? nodeSettingsFieldDefaults.allowCloudModelUnattendedRuns,
		allowCloudModelWebTools: response.allowCloudModelWebTools ?? nodeSettingsFieldDefaults.allowCloudModelWebTools,
		allowCloudModelMcpTools: response.allowCloudModelMcpTools ?? nodeSettingsFieldDefaults.allowCloudModelMcpTools,
		allowCloudModelSubAgents: response.allowCloudModelSubAgents ?? nodeSettingsFieldDefaults.allowCloudModelSubAgents,
		playbookAnalysisModelName: response.playbookAnalysisModelName ?? "",
		playbookEvalModelName: response.playbookEvalModelName ?? "",
		memoryExtractionModelName: response.memoryExtractionModelName ?? "",
		chatRetentionEnabled: response.chatRetentionEnabled ?? nodeSettingsFieldDefaults.chatRetentionEnabled,
		agentExecutionLogRetentionEnabled:
			response.agentExecutionLogRetentionEnabled ?? nodeSettingsFieldDefaults.agentExecutionLogRetentionEnabled,
		imageTextEncoderOnGpu: response.imageTextEncoderOnGpu ?? nodeSettingsFieldDefaults.imageTextEncoderOnGpu,
		...(Object.fromEntries(
			featureSwitchFields.map((field) => [field, response[field] ?? nodeSettingsFieldDefaults[field]]),
		) as Pick<NodeSettingsFieldsForm, (typeof featureSwitchFields)[number]>),
	};
}

// The three switches a profile sets. The page uses this to recognise a hand edit that must clear a pending preset.
const externalAccessBooleanFields = new Set<keyof NodeSettingsFieldsForm>([
	"autoCheckApplicationUpdates",
	"autoCheckRuntimeUpdates",
	"autoProvisionFirstRunModel",
]);

export function isExternalAccessBooleanField(field: keyof NodeSettingsFieldsForm): boolean {
	return externalAccessBooleanFields.has(field);
}

// Moves the draft onto a preset: the profile plus the triple it implies, so the operator sees what the server will
// write. The save still sends the profile NAME alone (see buildNodeSettingsRequest) — the server owns the derivation.
export function applyExternalAccessPreset(form: NodeSettingsFieldsForm, preset: ExternalAccessPreset): NodeSettingsFieldsForm {
	const enabled = preset === "recommended";
	return {
		...form,
		externalAccessProfile: preset,
		autoCheckApplicationUpdates: enabled,
		autoCheckRuntimeUpdates: enabled,
		autoProvisionFirstRunModel: enabled,
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
	readonly speculativeDraftGpuLayers: NumericBounds;
	readonly llamaChatCacheRamMiB: NumericBounds;
	readonly agentHomeMaxRunSeconds: NumericBounds;
	readonly agentHomeRunRetentionDays: NumericBounds;
	readonly agentHomeMaxInnerToolCalls: NumericBounds;
	readonly agentHomePatchApplyTimeoutSeconds: NumericBounds;
	readonly agentHomeRunRetentionMaxRuns: NumericBounds;
	readonly agentHomeRunRetentionMaxTotalBytes: NumericBounds;
	readonly huggingFaceDiskMarginBytes: NumericBounds;
	readonly tunables: Readonly<Record<TunableField, NumericBounds>>;
}

// Where each newer field's bounds live on the GET response. Typed against the generated response, so a renamed wire
// member is a compile error rather than a silent fallback. The two knowledge counts share one range.
const responseBoundKeys = {
	speculativeDraftGpuLayers: ["minSpeculativeDraftGpuLayers", "maxAllowedSpeculativeDraftGpuLayers"],
	llamaChatCacheRamMiB: ["minLlamaChatCacheRamMiB", "maxAllowedLlamaChatCacheRamMiB"],
	agentHomeMaxRunSeconds: ["minAgentHomeMaxRunSeconds", "maxAllowedAgentHomeMaxRunSeconds"],
	agentHomeRunRetentionDays: ["minAgentHomeRunRetentionDays", "maxAllowedAgentHomeRunRetentionDays"],
	agentHomeMaxInnerToolCalls: ["minAgentHomeMaxInnerToolCalls", "maxAllowedAgentHomeMaxInnerToolCalls"],
	agentHomePatchApplyTimeoutSeconds: ["minAgentHomePatchApplyTimeoutSeconds", "maxAllowedAgentHomePatchApplyTimeoutSeconds"],
	agentHomeRunRetentionMaxRuns: ["minAgentHomeRunRetentionMaxRuns", "maxAllowedAgentHomeRunRetentionMaxRuns"],
	agentHomeRunRetentionMaxTotalBytes: ["minAgentHomeRunRetentionMaxTotalBytes", "maxAllowedAgentHomeRunRetentionMaxTotalBytes"],
	imageMaxLoadedProcesses: ["minImageMaxLoadedProcesses", "maxAllowedImageMaxLoadedProcesses"],
	graphWorkflowMaxConcurrentRuns: ["minGraphWorkflowMaxConcurrentRuns", "maxAllowedGraphWorkflowMaxConcurrentRuns"],
	graphWorkflowDefaultNodeTimeoutSeconds: [
		"minGraphWorkflowDefaultNodeTimeoutSeconds",
		"maxAllowedGraphWorkflowDefaultNodeTimeoutSeconds",
	],
	workSessionMaxStepsPerRun: ["minWorkSessionMaxStepsPerRun", "maxAllowedWorkSessionMaxStepsPerRun"],
	workSessionMaxConcurrentSessions: ["minWorkSessionMaxConcurrentSessions", "maxAllowedWorkSessionMaxConcurrentSessions"],
	developmentMaxAttemptDurationSeconds: [
		"minDevelopmentMaxAttemptDurationSeconds",
		"maxAllowedDevelopmentMaxAttemptDurationSeconds",
	],
	developmentMaxToolCalls: ["minDevelopmentMaxToolCalls", "maxAllowedDevelopmentMaxToolCalls"],
	developmentMaxOutputTokens: ["minDevelopmentMaxOutputTokens", "maxAllowedDevelopmentMaxOutputTokens"],
	huggingFaceDiskMarginBytes: ["minHuggingFaceDiskMarginBytes", "maxAllowedHuggingFaceDiskMarginBytes"],
	llamaReadinessTimeoutCapSeconds: ["minLlamaReadinessTimeoutCapSeconds", "maxAllowedLlamaReadinessTimeoutCapSeconds"],
	llamaChatHttpTimeoutSeconds: ["minLlamaChatHttpTimeoutSeconds", "maxAllowedLlamaChatHttpTimeoutSeconds"],
	llamaEmbeddingHttpTimeoutSeconds: ["minLlamaEmbeddingHttpTimeoutSeconds", "maxAllowedLlamaEmbeddingHttpTimeoutSeconds"],
	llamaCpuThreadReserve: ["minLlamaCpuThreadReserve", "maxAllowedLlamaCpuThreadReserve"],
	llamaGpuReservePercent: ["minLlamaGpuReservePercent", "maxAllowedLlamaGpuReservePercent"],
	llamaRamReservePercent: ["minLlamaRamReservePercent", "maxAllowedLlamaRamReservePercent"],
	imageIdleTimeToLiveSeconds: ["minImageIdleTimeToLiveSeconds", "maxAllowedImageIdleTimeToLiveSeconds"],
	modelFitSafetyMarginPercent: ["minModelFitSafetyMarginPercent", "maxAllowedModelFitSafetyMarginPercent"],
	maxProviderCallsPerInvocation: ["minMaxProviderCallsPerInvocation", "maxAllowedMaxProviderCallsPerInvocation"],
	customToolMaxTimeoutSeconds: ["minCustomToolMaxTimeoutSeconds", "maxAllowedCustomToolMaxTimeoutSeconds"],
	webFetchTimeoutSeconds: ["minWebFetchTimeoutSeconds", "maxAllowedWebFetchTimeoutSeconds"],
	webFetchMaxContentChars: ["minWebFetchMaxContentChars", "maxAllowedWebFetchMaxContentChars"],
	knowledgeSearchDefaultResults: ["minKnowledgeSearchResults", "maxAllowedKnowledgeSearchResults"],
	knowledgeSearchMaxResults: ["minKnowledgeSearchResults", "maxAllowedKnowledgeSearchResults"],
	reasoningBudgetMinimalTokens: ["minReasoningBudgetTokens", "maxAllowedReasoningBudgetTokens"],
	reasoningBudgetLowTokens: ["minReasoningBudgetTokens", "maxAllowedReasoningBudgetTokens"],
	reasoningBudgetMediumTokens: ["minReasoningBudgetTokens", "maxAllowedReasoningBudgetTokens"],
	reasoningBudgetHighTokens: ["minReasoningBudgetTokens", "maxAllowedReasoningBudgetTokens"],
	chatOutputCapMaxTokens: ["minChatOutputCapMaxTokens", "maxAllowedChatOutputCapMaxTokens"],
	huggingFaceDownloadConnections: ["minHuggingFaceDownloadConnections", "maxAllowedHuggingFaceDownloadConnections"],
	transcriptionIdleTimeoutMinutes: ["minTranscriptionIdleTimeoutMinutes", "maxAllowedTranscriptionIdleTimeoutMinutes"],
	transcriptionInferenceTimeoutMinutes: [
		"minTranscriptionInferenceTimeoutMinutes",
		"maxAllowedTranscriptionInferenceTimeoutMinutes",
	],
	toolPipelineMaxIterationsPerRequest: [
		"minToolPipelineMaxIterationsPerRequest",
		"maxAllowedToolPipelineMaxIterationsPerRequest",
	],
	toolPipelineMaxToolResultChars: ["minToolPipelineMaxToolResultChars", "maxAllowedToolPipelineMaxToolResultChars"],
	toolPipelineMaxConsecutiveInvalidToolCalls: [
		"minToolPipelineMaxConsecutiveInvalidToolCalls",
		"maxAllowedToolPipelineMaxConsecutiveInvalidToolCalls",
	],
	defaultContextTokens: ["minDefaultContextTokens", "maxAllowedDefaultContextTokens"],
	providerBudgetRecentMessagesToKeep: ["minProviderBudgetRecentMessagesToKeep", "maxAllowedProviderBudgetRecentMessagesToKeep"],
	providerBudgetMaxCumulativeInputTokens: [
		"minProviderBudgetMaxCumulativeInputTokens",
		"maxAllowedProviderBudgetMaxCumulativeInputTokens",
	],
	contextBudgetRecentTurnKeepCount: ["minContextBudgetRecentTurnKeepCount", "maxAllowedContextBudgetRecentTurnKeepCount"],
	compactionAutoCompactPercent: ["minCompactionAutoCompactPercent", "maxAllowedCompactionAutoCompactPercent"],
	compactionRecentMessagesVerbatim: ["minCompactionRecentMessagesVerbatim", "maxAllowedCompactionRecentMessagesVerbatim"],
	maxInlinedAttachmentChars: ["minMaxInlinedAttachmentChars", "maxAllowedMaxInlinedAttachmentChars"],
	knowledgeChatTopK: ["minKnowledgeChatTopK", "maxAllowedKnowledgeChatTopK"],
	providerMaxRetries: ["minProviderMaxRetries", "maxAllowedProviderMaxRetries"],
	spawnMaxConcurrent: ["minSpawnMaxConcurrent", "maxAllowedSpawnMaxConcurrent"],
	spawnMaxCloud: ["minSpawnMaxCloud", "maxAllowedSpawnMaxCloud"],
	spawnQueueWaitSeconds: ["minSpawnQueueWaitSeconds", "maxAllowedSpawnQueueWaitSeconds"],
	knowledgeRetrievalLatencyBudgetMs: ["minKnowledgeRetrievalLatencyBudgetMs", "maxAllowedKnowledgeRetrievalLatencyBudgetMs"],
	knowledgeScheduledReindexIntervalMinutes: [
		"minKnowledgeScheduledReindexIntervalMinutes",
		"maxAllowedKnowledgeScheduledReindexIntervalMinutes",
	],
	chatRetentionDays: ["minRetentionDays", "maxAllowedRetentionDays"],
	agentExecutionLogRetentionDays: ["minRetentionDays", "maxAllowedRetentionDays"],
	nodeDbBackupRetainCount: ["minNodeDbBackupRetainCount", "maxAllowedNodeDbBackupRetainCount"],
	benchmarkKldCacheMaxBytes: ["minBenchmarkKldCacheMaxBytes", "maxAllowedBenchmarkKldCacheMaxBytes"],
	schedulerHistoryRetentionDays: ["minRetentionDays", "maxAllowedRetentionDays"],
} as const satisfies Record<string, readonly [keyof NodeSettingsResponse, keyof NodeSettingsResponse]>;

function responseBounds(response: NodeSettingsResponse | undefined, field: keyof typeof responseBoundKeys): NumericBounds {
	const [minKey, maxKey] = responseBoundKeys[field];
	return boundsOf(
		response?.[minKey] as number | undefined,
		response?.[maxKey] as number | undefined,
		nodeSettingsFieldBounds[field],
	);
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
		speculativeDraftGpuLayers: responseBounds(response, "speculativeDraftGpuLayers"),
		llamaChatCacheRamMiB: responseBounds(response, "llamaChatCacheRamMiB"),
		agentHomeMaxRunSeconds: responseBounds(response, "agentHomeMaxRunSeconds"),
		agentHomeRunRetentionDays: responseBounds(response, "agentHomeRunRetentionDays"),
		agentHomeMaxInnerToolCalls: responseBounds(response, "agentHomeMaxInnerToolCalls"),
		agentHomePatchApplyTimeoutSeconds: responseBounds(response, "agentHomePatchApplyTimeoutSeconds"),
		agentHomeRunRetentionMaxRuns: responseBounds(response, "agentHomeRunRetentionMaxRuns"),
		agentHomeRunRetentionMaxTotalBytes: responseBounds(response, "agentHomeRunRetentionMaxTotalBytes"),
		huggingFaceDiskMarginBytes: responseBounds(response, "huggingFaceDiskMarginBytes"),
		tunables: Object.fromEntries(tunableFields.map((field) => [field, responseBounds(response, field)])) as Record<
			TunableField,
			NumericBounds
		>,
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
//   speculativeDraftGpuLayers       — BuildSeededLlamaServerLaunchPolicyOptions
//   huggingFaceDiskMarginBytes      — BuildSeededHuggingFaceOptions
//   llamaReadinessTimeoutCap / llamaChatHttp / llamaEmbeddingHttp / llamaChatCacheRamMiB / llamaCpuThreadReserve /
//   llamaGpu+RamReservePercent / imageIdleTimeToLive / maxProviderCallsPerInvocation / huggingFaceDownloadConnections /
//   transcriptionIdle+InferenceTimeoutMinutes
//                                   — the synchronous INodeRuntimeSettings getters, each read once by a seed factory.
//   imageMaxLoadedProcesses / imageTextEncoderOnGpu
//                                   — AddNodeImagesExtensions host-build factory for StableDiffusionRuntimeOptions.
//   graphWorkflowMaxConcurrentRuns / graphWorkflowDefaultNodeTimeoutSeconds
//                                   — AddNodeGraphWorkflowsExtensions Configure<GraphWorkflowOptions>: the in-flight lanes
//                                     size their semaphores once.
//   workSessionMaxStepsPerRun / workSessionMaxConcurrentSessions
//                                   — AddNodeWorkSessionsExtensions Configure<WorkSessionOptions>: the supervisor sizes its
//                                     admission once.
//   developmentMaxAttemptDurationSeconds / developmentMaxToolCalls / developmentMaxOutputTokens
//                                   — AddNodeDevelopmentExtensions Configure<DevelopmentOptions>.
//   toolPipelineMaxIterationsPerRequest / toolPipelineMaxToolResultChars / toolPipelineMaxConsecutiveInvalidToolCalls
//                                   — AddNodeModelRuntimeExtensions Configure<AgentToolPipelineOptions>: the chat-client
//                                     pipeline and the tool wrappers are built once.
//   knowledgeScheduledReindexEnabled / knowledgeScheduledReindexIntervalMinutes
//                                   — AddNodeKnowledgeBaseExtensions Configure<KnowledgeBaseOptions>: the hosted reindex
//                                     worker reads both once at start.
//   developmentEnabled / schedulerEnabled
//                                   — NodeStartupSettings, read from node-settings.json BEFORE the host is built: the
//                                     Development endpoints and hub, and the Quartz scheduler, are registered or not.
// Live and NOT listed: modelFitSafetyMarginPercent, customToolMaxTimeoutSeconds, webFetch*, knowledgeSearch*,
// agentHomeMaxRunSeconds, agentHomeMaxInnerToolCalls, agentHomePatchApplyTimeoutSeconds, every agentHomeRunRetention* field
// (read per sweep) and containerRuntimeSelection (read per call); and the other chat knobs — defaultContextTokens,
// providerBudget*, contextBudgetRecentTurnKeepCount, compaction*, maxInlinedAttachmentChars, knowledgeChatTopK,
// providerRetryEnabled / providerMaxRetries and spawn* — each read per turn, job, send or root run; and the knowledge,
// privacy and usage knobs — knowledgeAdaptiveRerankingEnabled, knowledgeRetrievalLatencyBudgetMs, knowledgeAgentToolsEnabled,
// allowCloudModelAccess and the four other cloud-model permissions, the three background models, the two retention switches, every retention window,
// nodeDbBackupRetainCount and benchmarkKldCacheMaxBytes — each read per search, offer, turn, run, sweep or backup; and the
// other seven feature switches — FeatureSwitchMiddleware and every per-service check read them per request. Three carry
// help text instead of a badge: externalAppsEnabled (its container-bridge listener is bound at host build) and
// computeEnabled / devWorkflowsEnabled (their agent and workflow definitions are seeded at startup).
// Every other form field is read live on each call and must NOT be listed here: agentHome*, keepModelWarm*,
// toolCapableModels, enableTools, customToolsEnabled, toolRelevanceEnabled, webAccessEnabled, webSearchSearxngUrl,
// detachedGraceSeconds, usageRates, uiMode, voiceFeatureEnabled, defaultVoiceProfile, and the message-request timeout.
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
	"speculativeDraftGpuLayers",
	"huggingFaceDiskMarginBytes",
	"llamaReadinessTimeoutCapSeconds",
	"llamaChatHttpTimeoutSeconds",
	"llamaEmbeddingHttpTimeoutSeconds",
	"llamaChatCacheRamMiB",
	"llamaCpuThreadReserve",
	"llamaGpuReservePercent",
	"llamaRamReservePercent",
	"imageIdleTimeToLiveSeconds",
	"maxProviderCallsPerInvocation",
	"huggingFaceDownloadConnections",
	"transcriptionIdleTimeoutMinutes",
	"transcriptionInferenceTimeoutMinutes",
	"toolPipelineMaxIterationsPerRequest",
	"toolPipelineMaxToolResultChars",
	"toolPipelineMaxConsecutiveInvalidToolCalls",
	"knowledgeScheduledReindexEnabled",
	"knowledgeScheduledReindexIntervalMinutes",
	"imageMaxLoadedProcesses",
	"imageTextEncoderOnGpu",
	"graphWorkflowMaxConcurrentRuns",
	"graphWorkflowDefaultNodeTimeoutSeconds",
	"workSessionMaxStepsPerRun",
	"workSessionMaxConcurrentSessions",
	"developmentMaxAttemptDurationSeconds",
	"developmentMaxToolCalls",
	"developmentMaxOutputTokens",
	"developmentEnabled",
	"schedulerEnabled",
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

// A positive-long validator for the AgentHome byte caps (> 0), applied to the wire value.
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
// maps to messages. `pendingPreset` is the external-access profile the operator just picked and has not since
// overridden by hand; it makes the save a profile COMMAND rather than a field diff (see the external-access block).
export function buildNodeSettingsRequest(
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	bounds: NodeSettingsFieldBounds,
	includeDeveloperFields: boolean,
	pendingPreset: ExternalAccessPreset | null = null,
	effectiveToolCapableModels?: readonly string[],
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

	if (form.toolRelevanceEnabled !== baseline.toolRelevanceEnabled) {
		body.toolRelevanceEnabled = form.toolRelevanceEnabled;
	}

	// toolCapableModels — list editor; cleaned + validated. Compared by JSON for a stable change check.
	const toolModels = validateToolCapableModels(form.toolCapableModels);
	if (toolModels.hasInvalid) {
		errors["toolCapableModels"] = "invalid";
	} else if (JSON.stringify(toolModels.value) !== JSON.stringify(baseline.toolCapableModels)) {
		body.toolCapableModels = toolModels.value;
	}

	if (form.webAccessEnabled !== baseline.webAccessEnabled) {
		body.webAccessEnabled = form.webAccessEnabled;
	}

	if (form.webSearchSearxngUrl !== baseline.webSearchSearxngUrl) {
		const searxngUrl = validateOptionalHttpUrl(form.webSearchSearxngUrl);
		if (searxngUrl.error) {
			errors["webSearchSearxngUrl"] = searxngUrl.error;
		} else {
			// The PUT is null-preserving, so a blank field is sent as "" — the explicit clear back to DuckDuckGo.
			body.webSearchSearxngUrl = searxngUrl.value ?? "";
		}
	}

	if (form.ollamaEndpoint !== baseline.ollamaEndpoint) {
		const endpoint = validateOptionalHttpUrl(form.ollamaEndpoint);
		if (endpoint.error) {
			errors["ollamaEndpoint"] = endpoint.error;
		} else {
			// Null-preserving PUT, as for the SearXNG URL: a blank field is sent as "", which the node stores as unset and
			// resolves to its default endpoint. Null would keep the old value.
			body.ollamaEndpoint = endpoint.value ?? "";
		}
	}

	if (form.huggingFaceDefaultQuant !== baseline.huggingFaceDefaultQuant) {
		body.huggingFaceDefaultQuant = form.huggingFaceDefaultQuant.trim().length > 0 ? form.huggingFaceDefaultQuant.trim() : null;
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
		nodeSettingsDisplayScale.llamaIdleTimeToLiveSeconds,
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
		const idleTtl = toValidBoundedInt(
			toWireValue(form.llamaIdleTimeToLiveSeconds, nodeSettingsDisplayScale.llamaIdleTimeToLiveSeconds),
			bounds.llamaIdleTimeToLiveSeconds,
		);
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

	// External access — the profile and the three switches are NEVER sent together. A pending preset is a command: the
	// server writes that preset's triple. Otherwise only the switches the operator changed go out, and the server stamps
	// the profile "custom" itself. The client never computes "custom", and there is no client-side profile validator —
	// the value can only come from a closed-set Select, and the request validator is the trust boundary.
	if (pendingPreset !== null) {
		body.externalAccessProfile = pendingPreset;
	} else {
		// Explicit false is meaningful here exactly as it is for keepModelWarmEnabled: omission preserves the stored
		// value, while false switches the check off.
		if (form.autoCheckApplicationUpdates !== baseline.autoCheckApplicationUpdates) {
			body.autoCheckApplicationUpdates = form.autoCheckApplicationUpdates;
		}
		if (form.autoCheckRuntimeUpdates !== baseline.autoCheckRuntimeUpdates) {
			body.autoCheckRuntimeUpdates = form.autoCheckRuntimeUpdates;
		}
		if (form.autoProvisionFirstRunModel !== baseline.autoProvisionFirstRunModel) {
			body.autoProvisionFirstRunModel = form.autoProvisionFirstRunModel;
		}
	}

	if (form.uiMode !== baseline.uiMode) {
		body.uiMode = form.uiMode;
	}

	// The chat switches: explicit false is meaningful (it turns compaction, distillation or retries off).
	if (form.compactionAutoEnabled !== baseline.compactionAutoEnabled) {
		body.compactionAutoEnabled = form.compactionAutoEnabled;
	}
	if (form.compactionDistillEnabled !== baseline.compactionDistillEnabled) {
		body.compactionDistillEnabled = form.compactionDistillEnabled;
	}
	if (form.providerRetryEnabled !== baseline.providerRetryEnabled) {
		body.providerRetryEnabled = form.providerRetryEnabled;
	}

	// Knowledge, privacy, usage and image switches: explicit false is meaningful. The five cloud opt-ins are deliberately NOT
	// external-access preset switches, so a hand edit here never clears a pending preset.
	const roundTwoSwitches = [
		"knowledgeAdaptiveRerankingEnabled",
		"knowledgeScheduledReindexEnabled",
		"knowledgeAgentToolsEnabled",
		"allowCloudModelAccess",
		"allowCloudModelUnattendedRuns",
		"allowCloudModelWebTools",
		"allowCloudModelMcpTools",
		"allowCloudModelSubAgents",
		"chatRetentionEnabled",
		"agentExecutionLogRetentionEnabled",
		"imageTextEncoderOnGpu",
	] as const;
	for (const field of roundTwoSwitches) {
		if (form[field] !== baseline[field]) {
			body[field] = form[field];
		}
	}

	for (const field of featureSwitchFields) {
		if (form[field] !== baseline[field]) {
			body[field] = form[field];
		}
	}

	// Client mirror of the two NodeSettingsPolicy couplings, blamed on the same field the server blames: the switch being
	// turned on, or — when it was already on — what took its prerequisite away.
	if (form.devWorkflowsEnabled && !form.workSessionsEnabled) {
		errors[baseline.devWorkflowsEnabled ? "workSessionsEnabled" : "devWorkflowsEnabled"] = "requiresWorkSessions";
	}
	// The server judges the list the save leaves: a non-empty one, else the appsettings seed. Only an empty stored list lets
	// the effective list (`effectiveToolCapableModels`, GET agents/tool-capable-models) reveal that seed, so clearing a
	// non-empty list is left to the server's refusal on the field. Unknown reads as satisfied: a missed refusal still
	// comes back from the server, a false one would block every save.
	if (
		form.agentHomeEnabled &&
		toolModels.value.length === 0 &&
		baseline.toolCapableModels.length === 0 &&
		effectiveToolCapableModels?.length === 0
	) {
		errors[baseline.agentHomeEnabled ? "toolCapableModels" : "agentHomeEnabled"] ??= "requiresToolCapableModels";
	}

	// The background models: the empty string is the "inherit the default model" signal (the server stores null).
	const backgroundModelFields = ["playbookAnalysisModelName", "playbookEvalModelName", "memoryExtractionModelName"] as const;
	for (const field of backgroundModelFields) {
		if (form[field] !== baseline[field]) {
			body[field] = form[field].trim();
		}
	}

	if (form.voiceFeatureEnabled !== baseline.voiceFeatureEnabled) {
		// Explicit false is meaningful: it turns voice off for every user on the node.
		body.voiceFeatureEnabled = form.voiceFeatureEnabled;
	}

	// The voice picker offers no "none", so a blank draft only ever equals a blank baseline and is never sent.
	if (form.defaultVoiceProfile !== baseline.defaultVoiceProfile && form.defaultVoiceProfile.length > 0) {
		body.defaultVoiceProfile = form.defaultVoiceProfile;
	}

	for (const field of tunableFields) {
		if (unsettableTokenFields.has(field) && String(form[field]).trim() === "") {
			if (String(baseline[field]).trim() !== "") {
				body[field] = TOKEN_SETTING_UNSET;
			}
			continue;
		}
		collectBoundedInt(
			form[field],
			baseline[field],
			bounds.tunables[field],
			field,
			body,
			errors,
			(v) => {
				body[field] = v;
			},
			nodeSettingsScaleOf(field),
		);
	}

	// The default result count may not exceed the maximum; the server rejects the pair too (mapped onto the same field).
	if (
		form.knowledgeSearchDefaultResults !== baseline.knowledgeSearchDefaultResults ||
		form.knowledgeSearchMaxResults !== baseline.knowledgeSearchMaxResults
	) {
		const defaultResults = toValidBoundedInt(form.knowledgeSearchDefaultResults, bounds.tunables.knowledgeSearchDefaultResults);
		const maxResults = toValidBoundedInt(form.knowledgeSearchMaxResults, bounds.tunables.knowledgeSearchMaxResults);
		if (defaultResults !== undefined && maxResults !== undefined && defaultResults > maxResults) {
			errors["knowledgeSearchDefaultResults"] = "defaultAboveMax";
		}
	}

	collectChatCacheRam(form, baseline, bounds.llamaChatCacheRamMiB, body, errors);

	if (form.speculativeDraftGpuLayers !== baseline.speculativeDraftGpuLayers) {
		collectBoundedInt(
			form.speculativeDraftGpuLayers,
			baseline.speculativeDraftGpuLayers,
			bounds.speculativeDraftGpuLayers,
			"speculativeDraftGpuLayers",
			body,
			errors,
			(v) => {
				body.speculativeDraftGpuLayers = v;
			},
		);
	}

	collectBoundedInt(
		form.huggingFaceDiskMarginBytes,
		baseline.huggingFaceDiskMarginBytes,
		bounds.huggingFaceDiskMarginBytes,
		"huggingFaceDiskMarginBytes",
		body,
		errors,
		(v) => {
			body.huggingFaceDiskMarginBytes = v;
		},
		nodeSettingsDisplayScale.huggingFaceDiskMarginBytes,
	);

	if (form.containerRuntimeSelection !== baseline.containerRuntimeSelection) {
		body.containerRuntimeSelection = form.containerRuntimeSelection;
	}

	if (form.defaultReasoningEffort !== baseline.defaultReasoningEffort) {
		body.defaultReasoningEffort = form.defaultReasoningEffort;
	}

	if (form.chatOutputCapMode !== baseline.chatOutputCapMode) {
		body.chatOutputCapMode = form.chatOutputCapMode;
	}

	if (includeDeveloperFields) {
		collectBoundedInt(
			form.agentHomeMaxRunSeconds,
			baseline.agentHomeMaxRunSeconds,
			bounds.agentHomeMaxRunSeconds,
			"agentHomeMaxRunSeconds",
			body,
			errors,
			(v) => {
				body.agentHomeMaxRunSeconds = v;
			},
			nodeSettingsDisplayScale.agentHomeMaxRunSeconds,
		);
		collectBoundedInt(
			form.agentHomeRunRetentionDays,
			baseline.agentHomeRunRetentionDays,
			bounds.agentHomeRunRetentionDays,
			"agentHomeRunRetentionDays",
			body,
			errors,
			(v) => {
				body.agentHomeRunRetentionDays = v;
			},
		);
		collectBoundedInt(
			form.agentHomeRunRetentionMaxRuns,
			baseline.agentHomeRunRetentionMaxRuns,
			bounds.agentHomeRunRetentionMaxRuns,
			"agentHomeRunRetentionMaxRuns",
			body,
			errors,
			(v) => {
				body.agentHomeRunRetentionMaxRuns = v;
			},
		);
		collectBoundedInt(
			form.agentHomeRunRetentionMaxTotalBytes,
			baseline.agentHomeRunRetentionMaxTotalBytes,
			bounds.agentHomeRunRetentionMaxTotalBytes,
			"agentHomeRunRetentionMaxTotalBytes",
			body,
			errors,
			(v) => {
				body.agentHomeRunRetentionMaxTotalBytes = v;
			},
			nodeSettingsDisplayScale.agentHomeRunRetentionMaxTotalBytes,
		);
		collectBoundedInt(
			form.agentHomeMaxInnerToolCalls,
			baseline.agentHomeMaxInnerToolCalls,
			bounds.agentHomeMaxInnerToolCalls,
			"agentHomeMaxInnerToolCalls",
			body,
			errors,
			(v) => {
				body.agentHomeMaxInnerToolCalls = v;
			},
		);
		collectBoundedInt(
			form.agentHomePatchApplyTimeoutSeconds,
			baseline.agentHomePatchApplyTimeoutSeconds,
			bounds.agentHomePatchApplyTimeoutSeconds,
			"agentHomePatchApplyTimeoutSeconds",
			body,
			errors,
			(v) => {
				body.agentHomePatchApplyTimeoutSeconds = v;
			},
		);
		// A whole run must be allowed at least one full command; the server enforces the same on the merged values.
		if (
			form.agentHomeMaxRunSeconds !== baseline.agentHomeMaxRunSeconds ||
			form.agentHomeCommandTimeoutSeconds !== baseline.agentHomeCommandTimeoutSeconds
		) {
			const maxRun = toValidBoundedInt(
				toWireValue(form.agentHomeMaxRunSeconds, nodeSettingsDisplayScale.agentHomeMaxRunSeconds),
				bounds.agentHomeMaxRunSeconds,
			);
			const commandTimeout = toValidBoundedInt(
				toWireValue(form.agentHomeCommandTimeoutSeconds, nodeSettingsDisplayScale.agentHomeCommandTimeoutSeconds),
				bounds.agentHomeTimeoutSeconds,
			);
			if (maxRun !== undefined && commandTimeout !== undefined && maxRun < commandTimeout) {
				errors["agentHomeMaxRunSeconds"] = "belowCommandTimeout";
			}
		}
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
			nodeSettingsDisplayScale.agentHomePrepareTimeoutSeconds,
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
			nodeSettingsDisplayScale.agentHomeCommandTimeoutSeconds,
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
			nodeSettingsDisplayScale.agentHomeMaxSelectedFolderBytes,
		);
		collectPositiveLong(
			form.agentHomeMaxPatchBytes,
			baseline.agentHomeMaxPatchBytes,
			"agentHomeMaxPatchBytes",
			errors,
			(v) => {
				body.agentHomeMaxPatchBytes = v;
			},
			nodeSettingsDisplayScale.agentHomeMaxPatchBytes,
		);
	}

	return { body, errors };
}

// Prompt-cache RAM: compares what each side MEANS (auto / off / a size), so switching Custom on and back off without a
// net change sends nothing. Automatic is sent as the -1 reset sentinel, because null on the wire means "keep".
function collectChatCacheRam(
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
	bounds: NumericBounds,
	body: SaveNodeSettingsRequest,
	errors: Record<string, string>,
): void {
	const wireOf = (draft: NodeSettingsFieldsForm): number | string => {
		switch (draft.llamaChatCacheRamMode) {
			case "auto":
				return CHAT_CACHE_RAM_AUTO;
			case "off":
				return 0;
			default:
				return draft.llamaChatCacheRamMiB;
		}
	};
	const wire = wireOf(form);
	if (wire === wireOf(baseline)) {
		return;
	}
	if (form.llamaChatCacheRamMode !== "custom") {
		body.llamaChatCacheRamMiB = wire as number;
		return;
	}
	// A custom size is at least 1 MiB; 0 is the Off mode.
	const parsed = toValidBoundedInt(wire, { min: Math.max(1, bounds.min), max: bounds.max });
	if (parsed === undefined) {
		errors["llamaChatCacheRamMiB"] = "range";
		return;
	}
	body.llamaChatCacheRamMiB = parsed;
}

// Validates one bounded-int field against the baseline; on change either records an error or applies the parsed value
// via `apply`. `body`/`errors` are mutated in place (keeps the per-field call sites flat). `scale` converts a display
// value back to the wire unit the bounds are in (see nodeSettingsDisplayScale).
function collectBoundedInt(
	value: number | string,
	baseline: number | string,
	bounds: NumericBounds,
	field: string,
	_body: SaveNodeSettingsRequest,
	errors: Record<string, string>,
	apply: (parsed: number) => void,
	scale = 1,
): void {
	if (value === baseline) {
		return;
	}
	const parsed = toValidBoundedInt(toWireValue(value, scale), bounds);
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
	scale = 1,
): void {
	if (value === baseline) {
		return;
	}
	const parsed = toValidPositiveLong(toWireValue(value, scale));
	if (parsed === undefined) {
		errors[field] = "positive";
		return;
	}
	apply(parsed);
}

// What the save bar reports: every field the next Save would send, plus every edited field that currently fails
// validation (it is still an unsaved change, it just cannot go out yet), and the restart-gated subset of those.
export interface NodeSettingsPendingChanges {
	readonly changed: readonly (keyof NodeSettingsFieldsForm)[];
	readonly restartRequired: readonly (keyof NodeSettingsFieldsForm)[];
}

export function summarizePendingChanges(
	result: NodeSettingsValidationResult,
	form: NodeSettingsFieldsForm,
	baseline: NodeSettingsFieldsForm,
): NodeSettingsPendingChanges {
	const changed = new Set(Object.keys(result.body) as (keyof NodeSettingsFieldsForm)[]);
	for (const key of Object.keys(result.errors)) {
		const field = key as keyof NodeSettingsFieldsForm;
		if (JSON.stringify(form[field]) !== JSON.stringify(baseline[field])) {
			changed.add(field);
		}
	}
	const list = [...changed];
	return { changed: list, restartRequired: list.filter((field) => restartGatedNodeSettingsFields.has(field)) };
}
