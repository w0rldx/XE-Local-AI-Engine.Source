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
    /// <summary>
    ///     Wire name to the value the host read, so the pending-restart comparison stays one table. A switch with a
    ///     configuration seed reads its effective value: an unset switch and the saved seed are the same running node.
    /// </summary>
    internal static readonly FrozenDictionary<string, Func<StoredNodeSettings, NodeSettingsEffectiveValues, object?>> StoredValues =
        new Dictionary<string, Func<StoredNodeSettings, NodeSettingsEffectiveValues, object?>>(StringComparer.Ordinal)
        {
            ["defaultModelName"] = static (s, _) => s.DefaultModelName, // ResolveChatConnectionSettings; InvocationRunner constructor
            ["ollamaEndpoint"] = static (s, _) => s.OllamaEndpoint, // ResolveChatConnectionSettings
            ["huggingFaceDefaultQuant"] = static (s, _) => s.HuggingFaceDefaultQuant, // BuildSeededHuggingFaceOptions
            ["huggingFaceDiskMarginBytes"] = static (s, _) => s.HuggingFaceDiskMarginBytes, // BuildSeededHuggingFaceOptions
            ["huggingFaceDownloadConnections"] = static (s, _) => s.HuggingFaceDownloadConnections, // BuildSeededHuggingFaceOptions
            ["llamaMaxLoadedProcesses"] = static (s, _) => s.LlamaMaxLoadedProcesses, // BuildSeededLlamaServerSupervisorOptions
            ["llamaIdleTimeToLiveSeconds"] = static (s, _) => s.LlamaIdleTimeToLiveSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["chatCacheReuse"] = static (s, _) => s.ChatCacheReuse, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeMode"] = static (s, _) => s.SpeculativeMode, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftModelName"] = static (s, _) => s.SpeculativeDraftModelName, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftMaxTokens"] = static (s, _) => s.SpeculativeDraftMaxTokens, // BuildSeededLlamaServerSupervisorOptions
            ["speculativeDraftGpuLayers"] = static (s, _) => s.SpeculativeDraftGpuLayers, // BuildSeededLlamaServerSupervisorOptions
            ["llamaReadinessTimeoutCapSeconds"] = static (s, _) => s.LlamaReadinessTimeoutCapSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaChatHttpTimeoutSeconds"] = static (s, _) => s.LlamaChatHttpTimeoutSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaEmbeddingHttpTimeoutSeconds"] = static (s, _) => s.LlamaEmbeddingHttpTimeoutSeconds, // BuildSeededLlamaServerSupervisorOptions
            ["llamaChatCacheRamMiB"] = static (s, _) => s.LlamaChatCacheRamMiB, // BuildSeededLlamaServerSupervisorOptions
            ["kvCacheType"] = static (s, _) => s.KvCacheType, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaCpuThreadReserve"] = static (s, _) => s.LlamaCpuThreadReserve, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaGpuReservePercent"] = static (s, _) => s.LlamaGpuReservePercent, // BuildSeededLlamaServerLaunchPolicyOptions
            ["llamaRamReservePercent"] = static (s, _) => s.LlamaRamReservePercent, // BuildSeededLlamaServerLaunchPolicyOptions
            ["orchestrationIdleTimeoutSeconds"] = static (s, _) => s.OrchestrationIdleTimeoutSeconds, // OrchestrationAgentOptions seed, AddNodeModelRuntime
            ["maxProviderCallsPerInvocation"] = static (s, _) => s.MaxProviderCallsPerInvocation, // ProviderCallBudgetOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxIterationsPerRequest"] = static (s, _) => s.ToolPipelineMaxIterationsPerRequest, // AgentToolPipelineOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxToolResultChars"] = static (s, _) => s.ToolPipelineMaxToolResultChars, // AgentToolPipelineOptions seed, AddNodeModelRuntime
            ["toolPipelineMaxConsecutiveInvalidToolCalls"] = static (s, _) => s.ToolPipelineMaxConsecutiveInvalidToolCalls, // AgentToolPipelineOptions seed
            ["maxResponseSizeMb"] = static (s, _) => s.MaxResponseSizeMb, // InvocationRunner constructor
            ["maxPendingToolCallAgeMinutes"] = static (s, _) => s.MaxPendingToolCallAgeMinutes, // InvocationRunner, ToolApprovalCoordinator, InvocationLifecycleTracker
            ["transcriptionIdleTimeoutMinutes"] = static (s, _) => s.TranscriptionIdleTimeoutMinutes, // WhisperCpp runtime options seed, AddNodeTranscription
            ["transcriptionInferenceTimeoutMinutes"] = static (s, _) => s.TranscriptionInferenceTimeoutMinutes, // WhisperCpp runtime options seed
            ["imageIdleTimeToLiveSeconds"] = static (s, _) => s.ImageIdleTimeToLiveSeconds, // StableDiffusion runtime options seed, AddNodeImages
            ["imageMaxLoadedProcesses"] = static (s, _) => s.ImageMaxLoadedProcesses, // StableDiffusion runtime options seed, AddNodeImages
            ["imageTextEncoderOnGpu"] = static (_, e) => e.ImageTextEncoderOnGpu, // StableDiffusion runtime options seed, AddNodeImages
            ["knowledgeScheduledReindexEnabled"] = static (_, e) => e.KnowledgeScheduledReindexEnabled, // KnowledgeBaseOptions seed, AddNodeKnowledgeBase
            ["knowledgeScheduledReindexIntervalMinutes"] = static (s, _) => s.KnowledgeScheduledReindexIntervalMinutes, // KnowledgeBaseOptions seed
            ["graphWorkflowMaxConcurrentRuns"] = static (s, _) => s.GraphWorkflowMaxConcurrentRuns, // GraphWorkflowOptions seed, AddNodeGraphWorkflows
            ["graphWorkflowDefaultNodeTimeoutSeconds"] = static (s, _) => s.GraphWorkflowDefaultNodeTimeoutSeconds, // GraphWorkflowOptions seed
            ["workSessionMaxStepsPerRun"] = static (s, _) => s.WorkSessionMaxStepsPerRun, // WorkSessionOptions seed, AddNodeWorkSessions
            ["workSessionMaxConcurrentSessions"] = static (s, _) => s.WorkSessionMaxConcurrentSessions, // WorkSessionOptions seed, AddNodeWorkSessions
            ["developmentMaxAttemptDurationSeconds"] = static (s, _) => s.DevelopmentMaxAttemptDurationSeconds, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentMaxToolCalls"] = static (s, _) => s.DevelopmentMaxToolCalls, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentMaxOutputTokens"] = static (s, _) => s.DevelopmentMaxOutputTokens, // DevelopmentOptions seed, AddNodeDevelopment
            ["developmentEnabled"] = static (_, e) => e.DevelopmentEnabled, // NodeStartupSettings.DevelopmentEnabled: what the host registers
            ["schedulerEnabled"] = static (_, e) => e.SchedulerEnabled, // NodeStartupSettings.SchedulerEnabled: whether Quartz is registered
            ["externalAppsEnabled"] = static (_, e) => e.ExternalAppsEnabled // NodeStartupSettings.ExternalAppsEnabled: the container bridge
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The restart-gated wire field names, in ordinal order.</summary>
    public static IReadOnlyList<string> Fields { get; } = [.. StoredValues.Keys.Order(StringComparer.Ordinal)];
}
