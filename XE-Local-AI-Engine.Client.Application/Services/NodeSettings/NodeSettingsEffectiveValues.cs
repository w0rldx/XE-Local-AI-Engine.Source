namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

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

    public required bool ChatRetentionEnabled { get; init; }

    public required bool AgentExecutionLogRetentionEnabled { get; init; }

    public required bool ImageTextEncoderOnGpu { get; init; }

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

    public required bool DevWorkflowsEnabled { get; init; }
}
