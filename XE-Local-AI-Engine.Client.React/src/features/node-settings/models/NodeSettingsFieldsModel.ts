import type { XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse } from "@/core/api/generated";
import { KV_CACHE_TYPE_DEFAULT, SPECULATIVE_DISABLED_MODE } from "@/features/node-settings/models/NodeSettingsRuntimeModes";
import { toUsageRateRows, type UsageRateRow } from "@/features/node-settings/models/NodeSettingsUsageRateModel";

// The migrated appsettings knobs as editable form state. Numbers are kept as `number | string` so an in-progress
// edit (an empty input, a partial number) survives in the controlled NumberInput without being coerced; validation
// resolves the string at save time. Each field is schema-validated against the server-provided Min/Max bounds before
// being sent, and only changed fields are included in the PUT (optional request semantics: omit = keep current).

// An installed model offered in one of the settings-form model pickers (draft, keep-warm, auto-effort fast, reranker).
// `value` is the model name; the backend resolves it to a path at save.
export interface NodeSettingsModelOption {
	readonly value: string;
	readonly label: string;
}

// The external-access profiles an operator can actually pick. The backend stores two more literals the client never
// sends: "custom", which it stamps when a save carries individual switches and no profile (display-only, rendered as a
// disabled option on the settings Select), and "pending", which the first-run route intercepts. Naming only the two
// choosable ones here makes "the client never computes custom" a compile error rather than a runtime guard.
export type ExternalAccessPreset = "recommended" | "offline";

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
	// Knowledge-base embedding pin (empty = not pinned yet; the node pins the model of the first index)
	knowledgeEmbeddingModelName: string;
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
	// The sandbox security profile ("low" / "high", or "pending" / "" before the first-run chooser answered it). Lowering it
	// is confirmed in the save flow (see lowersSandboxSecurityProfile).
	sandboxSecurityProfile: string;
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
	// that blanks a stored value sends null, which clears it.
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
	// size. Only the size key exists on the wire; a save that returns to automatic sends null.
	llamaChatCacheRamMode: ChatCacheRamMode;
	llamaChatCacheRamMiB: number | string;
	// Blank = never set (the runtime default). The wire could clear it with null, but this form has no "Use default" for
	// it, so a blank edit is refused.
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
	// The intent router graph workflow id; "" routes on aliases alone (the save sends null to clear it). Read per request.
	intentRouterDefinitionId: string;
	// Blank = the node's shipped default (shippedDefaults); the save sends null to reset. Read per request.
	intentMaxUtteranceAgeSeconds: number | string;
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
	// Security-widening: lets Preview sandbox mechanisms serve run_python and sandboxed MCP. Turning it on is confirmed in the
	// save flow (see turnsOnExecutionPreviews).
	executionPreviewsEnabled: boolean;
}

// The feature switches in display order. Explicit false is meaningful (it turns the feature off).
export const featureSwitchFields = [
	"developmentEnabled",
	"workSessionsEnabled",
	"graphWorkflowsEnabled",
	"agentHomeEnabled",
	"computeEnabled",
	"externalAppsEnabled",
	"transcriptionEnabled",
	"schedulerEnabled",
	"executionPreviewsEnabled",
] as const satisfies readonly (keyof NodeSettingsFieldsForm)[];

export type ChatCacheRamMode = "auto" | "off" | "custom";

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
	intentMaxUtteranceAgeSeconds: 30,
	defaultReasoningEffort: "low",
	chatOutputCapMode: "cap",
} as const;

// The curated tunables that are plain bounded integers, each validated against its own server bounds entry.
export const tunableFields = [
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
	"intentMaxUtteranceAgeSeconds",
	"workSessionMaxStepsPerRun",
	"workSessionMaxConcurrentSessions",
	"developmentMaxAttemptDurationSeconds",
	"developmentMaxToolCalls",
	"developmentMaxOutputTokens",
] as const;

export type TunableField = (typeof tunableFields)[number];

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
	knowledgeEmbeddingModelName: "",
	autoEffortFastModelName: "",
	usageRates: [],
	// No profile until the node has one: "" is what an absent server value seeds too (see toNodeSettingsFieldsForm),
	// so the Select renders its "Not chosen" placeholder rather than claiming a decision the node never made.
	externalAccessProfile: "",
	autoCheckApplicationUpdates: true,
	autoCheckRuntimeUpdates: true,
	autoProvisionFirstRunModel: true,
	uiMode: "",
	sandboxSecurityProfile: "",
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
	intentRouterDefinitionId: "",
	intentMaxUtteranceAgeSeconds: "",
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
	executionPreviewsEnabled: false,
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
		knowledgeEmbeddingModelName: response.knowledgeEmbeddingModelName ?? "",
		autoEffortFastModelName: response.autoEffortFastModelName ?? "",
		usageRates: toUsageRateRows(response.usageRates),
		// An absent profile is genuinely undecided (a corrupted settings file reads as null), so it renders as an
		// unselected Select rather than being coerced to "recommended" — the node must not claim a choice it never made.
		externalAccessProfile: response.externalAccessProfile ?? "",
		autoCheckApplicationUpdates: response.autoCheckApplicationUpdates ?? nodeSettingsFieldDefaults.autoCheckApplicationUpdates,
		autoCheckRuntimeUpdates: response.autoCheckRuntimeUpdates ?? nodeSettingsFieldDefaults.autoCheckRuntimeUpdates,
		autoProvisionFirstRunModel: response.autoProvisionFirstRunModel ?? nodeSettingsFieldDefaults.autoProvisionFirstRunModel,
		uiMode: response.uiMode ?? "",
		// "pending" is a fresh node's default, enforced as low; showing it as low keeps a Low pick from reading as a change.
		sandboxSecurityProfile: response.sandboxSecurityProfile === "pending" ? "low" : (response.sandboxSecurityProfile ?? ""),
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
		intentRouterDefinitionId: response.intentRouterDefinitionId ?? "",
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

// The three switches a profile sets. The page uses this to recognize a hand edit that must clear a pending preset.
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
