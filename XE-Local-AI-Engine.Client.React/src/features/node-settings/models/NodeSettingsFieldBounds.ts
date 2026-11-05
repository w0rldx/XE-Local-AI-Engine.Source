import type { XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse } from "@/core/api/generated";
import { type TunableField, tunableFields } from "@/features/node-settings/models/NodeSettingsFieldsModel";

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
	intentMaxUtteranceAgeSeconds: { min: 5, max: 600 },
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

// Wire bounds expressed in the display unit, for a field's range text and its NumberInput min/max.
export function toDisplayBounds(bounds: NumericBounds, scale: number): NumericBounds {
	return { min: bounds.min / scale, max: bounds.max / scale };
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

// Where each field's bounds live on the GET response. Typed against the generated response, so a renamed wire
// member is a compile error rather than a silent fallback. The two knowledge counts share one range.
const responseBoundKeys = {
	llamaMaxLoadedProcesses: ["minLlamaMaxLoadedProcesses", "maxAllowedLlamaMaxLoadedProcesses"],
	llamaIdleTimeToLiveSeconds: ["minLlamaIdleTimeToLiveSeconds", "maxAllowedLlamaIdleTimeToLiveSeconds"],
	keepModelWarmIntervalSeconds: ["minKeepModelWarmIntervalSeconds", "maxAllowedKeepModelWarmIntervalSeconds"],
	maxResponseSizeMb: ["minMaxResponseSizeMb", "maxAllowedMaxResponseSizeMb"],
	chatCacheReuse: ["minChatCacheReuse", "maxAllowedChatCacheReuse"],
	speculativeDraftMaxTokens: ["minSpeculativeDraftMaxTokens", "maxAllowedSpeculativeDraftMaxTokens"],
	orchestrationIdleTimeoutSeconds: ["minOrchestrationIdleTimeoutSeconds", "maxAllowedOrchestrationIdleTimeoutSeconds"],
	agentHomeTimeoutSeconds: ["minAgentHomeTimeoutSeconds", "maxAllowedAgentHomeTimeoutSeconds"],
	maxPendingToolCallAgeMinutes: ["minMaxPendingToolCallAgeMinutes", "maxAllowedMaxPendingToolCallAgeMinutes"],
	detachedGraceSeconds: ["minDetachedGraceSeconds", "maxAllowedDetachedGraceSeconds"],
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
	intentMaxUtteranceAgeSeconds: ["minIntentMaxUtteranceAgeSeconds", "maxAllowedIntentMaxUtteranceAgeSeconds"],
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

// A field's effective bounds: the server-provided value, else the hardcoded fallback range.
function responseBounds(response: NodeSettingsResponse | undefined, field: keyof typeof responseBoundKeys): NumericBounds {
	const [minKey, maxKey] = responseBoundKeys[field];
	const fallback = nodeSettingsFieldBounds[field];
	return {
		min: (response?.[minKey] as number | undefined) ?? fallback.min,
		max: (response?.[maxKey] as number | undefined) ?? fallback.max,
	};
}

export function toNodeSettingsFieldBounds(response: NodeSettingsResponse | undefined): NodeSettingsFieldBounds {
	return {
		llamaMaxLoadedProcesses: responseBounds(response, "llamaMaxLoadedProcesses"),
		llamaIdleTimeToLiveSeconds: responseBounds(response, "llamaIdleTimeToLiveSeconds"),
		keepModelWarmIntervalSeconds: responseBounds(response, "keepModelWarmIntervalSeconds"),
		maxResponseSizeMb: responseBounds(response, "maxResponseSizeMb"),
		chatCacheReuse: responseBounds(response, "chatCacheReuse"),
		speculativeDraftMaxTokens: responseBounds(response, "speculativeDraftMaxTokens"),
		orchestrationIdleTimeoutSeconds: responseBounds(response, "orchestrationIdleTimeoutSeconds"),
		agentHomeTimeoutSeconds: responseBounds(response, "agentHomeTimeoutSeconds"),
		maxPendingToolCallAgeMinutes: responseBounds(response, "maxPendingToolCallAgeMinutes"),
		detachedGraceSeconds: responseBounds(response, "detachedGraceSeconds"),
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
