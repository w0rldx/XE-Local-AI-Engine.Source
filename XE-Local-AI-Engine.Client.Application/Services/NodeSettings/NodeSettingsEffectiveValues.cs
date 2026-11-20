namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The effective value (stored, else the configuration seed) of every migrated node-settings switch and retention window,
///     so the settings page shows, and can change, a value an appsettings seed set but nobody ever saved.
/// </summary>
public sealed record NodeSettingsEffectiveValues
{
    public required bool CompactionAutoEnabled { get; init; }

    public required bool CompactionDistillEnabled { get; init; }

    public required bool ProviderRetryEnabled { get; init; }

    public required bool KnowledgeAdaptiveRerankingEnabled { get; init; }

    public required bool KnowledgeScheduledReindexEnabled { get; init; }

    public required bool KnowledgeAgentToolsEnabled { get; init; }

    public required bool AllowCloudModelAccess { get; init; }

    public required bool AllowCloudModelUnattendedRuns { get; init; }

    public required bool AllowCloudModelWebTools { get; init; }

    public required bool AllowCloudModelMcpTools { get; init; }

    public required bool AllowCloudModelSubAgents { get; init; }

    public required bool ChatRetentionEnabled { get; init; }

    public required bool AgentExecutionLogRetentionEnabled { get; init; }

    public required bool ImageTextEncoderOnGpu { get; init; }

    /// <summary>The llama.cpp chat-process cap the supervisor runs with. Not on the wire: the node-settings save applies it.</summary>
    public required int LlamaMaxLoadedProcesses { get; init; }

    /// <summary>The llama.cpp idle time-to-live the supervisor runs with. Not on the wire: the node-settings save applies it.</summary>
    public required TimeSpan LlamaIdleTimeToLive { get; init; }

    /// <summary>The sd-server process cap the image supervisor runs with. Not on the wire: the node-settings save applies it.</summary>
    public required int ImageMaxLoadedProcesses { get; init; }

    /// <summary>The sd-server idle time-to-live the image supervisor runs with. Not on the wire: the node-settings save applies it.</summary>
    public required TimeSpan ImageIdleTimeToLive { get; init; }

    /// <summary>The keep-model-warm interval, so the save policy compares it with the TTL of the same record. Not on the wire.</summary>
    public required TimeSpan KeepModelWarmInterval { get; init; }

    public required int ChatRetentionDays { get; init; }

    public required int AgentExecutionLogRetentionDays { get; init; }

    public required int SchedulerHistoryRetentionDays { get; init; }

    public required int AgentHomeRunRetentionDays { get; init; }

    public required bool DevelopmentEnabled { get; init; }

    public required bool WorkSessionsEnabled { get; init; }

    public required bool GraphWorkflowsEnabled { get; init; }

    public required bool TranscriptionEnabled { get; init; }

    public required bool ExternalAppsEnabled { get; init; }

    public required bool ComputeEnabled { get; init; }

    public required bool AgentHomeEnabled { get; init; }

    public required bool SchedulerEnabled { get; init; }

    public required bool ExecutionPreviewsEnabled { get; init; }

    /// <summary>The profile every sandbox gate reads: <c>high</c> only when the stored literal is <c>high</c>. There is no configuration seed.</summary>
    public required SandboxSecurityProfile SandboxSecurityProfile { get; init; }
}
