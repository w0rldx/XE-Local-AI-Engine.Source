namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Collections.Frozen;

/// <summary>
///     The stored node settings the host reads once while it builds, by camelCase wire name: a change to one applies
///     after a node restart.
/// </summary>
/// <remarks>
///     Each entry names its read-once site. The Node Settings DTO docs say "Applies after a node restart" for exactly
///     these fields, and a test holds the two equal, so a new read-once site and its doc line land together.
/// </remarks>
public static class NodeSettingsRestartGate
{
    /// <summary>Wire name to the stored value the host read, so the pending-restart comparison stays one table.</summary>
    internal static readonly FrozenDictionary<string, Func<StoredNodeSettings, object?>> StoredValues =
        new Dictionary<string, Func<StoredNodeSettings, object?>>(StringComparer.Ordinal)
        {
            ["defaultModelName"] = static s => s.DefaultModelName, // ResolveChatConnectionSettings; InvocationRunner constructor
            ["ollamaEndpoint"] = static s => s.OllamaEndpoint, // ResolveChatConnectionSettings
            ["huggingFaceDefaultQuant"] = static s => s.HuggingFaceDefaultQuant, // BuildSeededHuggingFaceOptions
            ["huggingFaceDiskMarginBytes"] = static s => s.HuggingFaceDiskMarginBytes, // BuildSeededHuggingFaceOptions
            ["huggingFaceDownloadConnections"] = static s => s.HuggingFaceDownloadConnections, // BuildSeededHuggingFaceOptions
            ["llamaMaxLoadedProcesses"] = static s => s.LlamaMaxLoadedProcesses, // BuildSeededLlamaServerSupervisorOptions
            ["llamaIdleTimeToLiveSeconds"] = static s => s.LlamaIdleTimeToLiveSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["chatCacheReuse"] = static s => s.ChatCacheReuse, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeMode"] = static s => s.SpeculativeMode, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftModelName"] = static s => s.SpeculativeDraftModelName, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftMaxTokens"] = static s => s.SpeculativeDraftMaxTokens, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftGpuLayers"] = static s => s.SpeculativeDraftGpuLayers, // BuildSeededLlamaServerSupervisorOptions
            ["llamaReadinessTimeoutCapSeconds"] = static s => s.LlamaReadinessTimeoutCapSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaChatHttpTimeoutSeconds"] = static s => s.LlamaChatHttpTimeoutSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaEmbeddingHttpTimeoutSeconds"] = static s => s.LlamaEmbeddingHttpTimeoutSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaChatCacheRamMiB"] = static s => s.LlamaChatCacheRamMiB, // BuildSeededLlamaServerSupervisorOptions
            ["kvCacheType"] = static s => s.KvCacheType, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaCpuThreadReserve"] = static s => s.LlamaCpuThreadReserve, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaGpuReservePercent"] = static s => s.LlamaGpuReservePercent, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaRamReservePercent"] = static s => s.LlamaRamReservePercent, // BuildSeededLlamaServerLaunchPolicyOptions
            ["orchestrationIdleTimeoutSeconds"] = static s => s.OrchestrationIdleTimeoutSeconds, // OrchestrationAgentOptions seed, AddNodeModelRuntime
            ["maxProviderCallsPerInvocation"] = static s => s.MaxProviderCallsPerInvocation, // ProviderCallBudgetOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxIterationsPerRequest"] = static s => s.ToolPipelineMaxIterationsPerRequest, // AgentToolPipelineOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxToolResultChars"] = static s => s.ToolPipelineMaxToolResultChars, // AgentToolPipelineOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxConsecutiveInvalidToolCalls"] = static s => s.ToolPipelineMaxConsecutiveInvalidToolCalls, // AgentToolPipelineOptions seed
            ["maxResponseSizeMb"] = static s => s.MaxResponseSizeMb, // InvocationRunner constructor
            ["maxPendingToolCallAgeMinutes"] = static s => s.MaxPendingToolCallAgeMinutes, // InvocationRunner, ToolApprovalCoordinator, InvocationLifecycleTracker
            ["transcriptionIdleTimeoutMinutes"] = static s => s.TranscriptionIdleTimeoutMinutes, // WhisperCpp runtime options seed, AddNodeTranscription
            ["transcriptionInferenceTimeoutMinutes"] = static s => s.TranscriptionInferenceTimeoutMinutes, // WhisperCpp runtime options seed
            ["imageIdleTimeToLiveSeconds"] = static s => s.ImageIdleTimeToLiveSeconds, // StableDiffusion runtime options seed, AddNodeImages
            ["imageMaxLoadedProcesses"] = static s => s.ImageMaxLoadedProcesses, // StableDiffusion runtime options seed, AddNodeImages
            ["imageTextEncoderOnGpu"] = static s => s.ImageTextEncoderOnGpu, // StableDiffusion runtime options seed, AddNodeImages
            ["knowledgeScheduledReindexEnabled"] = static s => s.KnowledgeScheduledReindexEnabled, // KnowledgeBaseOptions seed, AddNodeKnowledgeBase
            ["knowledgeScheduledReindexIntervalMinutes"] = static s => s.KnowledgeScheduledReindexIntervalMinutes, // KnowledgeBaseOptions seed
            ["graphWorkflowMaxConcurrentRuns"] = static s => s.GraphWorkflowMaxConcurrentRuns, // GraphWorkflowOptions seed, AddNodeGraphWorkflows
            ["graphWorkflowDefaultNodeTimeoutSeconds"] = static s => s.GraphWorkflowDefaultNodeTimeoutSeconds, // GraphWorkflowOptions seed
            ["workSessionMaxStepsPerRun"] = static s => s.WorkSessionMaxStepsPerRun, // WorkSessionOptions seed, AddNodeWorkSessions
            ["workSessionMaxConcurrentSessions"] = static s => s.WorkSessionMaxConcurrentSessions, // WorkSessionOptions seed, AddNodeWorkSessions
            ["developmentMaxAttemptDurationSeconds"] = static s => s.DevelopmentMaxAttemptDurationSeconds, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentMaxToolCalls"] = static s => s.DevelopmentMaxToolCalls, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentMaxOutputTokens"] = static s => s.DevelopmentMaxOutputTokens, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentEnabled"] = static s => s.DevelopmentEnabled, // NodeStartupSettings.DevelopmentEnabled: what the host registers
            ["schedulerEnabled"] = static s => s.SchedulerEnabled, // NodeStartupSettings.SchedulerEnabled: whether Quartz is registered
            ["externalAppsEnabled"] = static s => s.ExternalAppsEnabled // NodeStartupSettings.ExternalAppsEnabled: the container bridge
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The restart-gated wire field names, in ordinal order.</summary>
    public static IReadOnlyList<string> Fields { get; } = [.. StoredValues.Keys.Order(StringComparer.Ordinal)];
}
