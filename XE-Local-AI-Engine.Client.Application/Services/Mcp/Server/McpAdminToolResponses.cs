namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public static class McpAdminToolFailureCodes
{
    public const string AgentNotFound = "agent_not_found";
    public const string Busy = "busy";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidVariant = "invalid_variant";
    public const string ModelPullConflict = "model_pull_conflict";
    public const string ModelPullFailed = "model_pull_failed";
    public const string ModelPullNotFound = "model_pull_not_found";
    public const string ValidationFailed = "validation_failed";
}

public sealed record McpNodeStatusResponse(string Version, long UptimeSeconds, string? DefaultModelName, int LoadedProcessCount);

public sealed record McpRuntimeStatusResponse(
    string? InstalledTag,
    string RecommendedTag,
    string? UpstreamLatestTag,
    bool UpdateAvailable,
    bool IsOffline,
    int LoadedProcessCount);

public sealed record McpRuntimeAcquisitionStartResponse(
    string Status,
    string? Variant,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpRuntimeAcquisitionResponse(
    long Sequence,
    string Phase,
    string? Variant,
    string? Tag,
    long? CompletedBytes,
    long? TotalBytes,
    int StepIndex,
    int StepCount,
    string? SanitizedError);

public sealed record McpModelPullStartResponse(
    string Status,
    string? ModelName,
    string? OperationId,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpModelPullResponse(
    string Status,
    string? ModelName,
    string? Phase,
    long? CompletedBytes,
    long? TotalBytes,
    string? SanitizedError,
    string? OperationId,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpModelPullCancelResponse(bool Cancelled);

public sealed record McpModelDeleteResponse(
    bool Deleted,
    string? ModelName,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpDefaultModelResponse(
    bool Updated,
    string? SelectedModelName,
    string? PreviousDefault,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpNodeSettingsUpdateResponse(
    bool Updated,
    IReadOnlyList<string> RejectedFields,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpAgentResponse(
    string Status,
    McpAgentDefinition? Agent,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record McpAgentDeleteResponse(bool Deleted, string? FailureCode = null, string? DisplayMessage = null);

public sealed record McpGenerationMetadataInput
{
    public string? Model { get; init; }
    public string Mode { get; init; } = "create";
    public string? UserBrief { get; init; }
    public string? Rationale { get; init; }
    public IReadOnlyList<string>? Assumptions { get; init; }
    public double Confidence { get; init; }
    public long GeneratedAtUtc { get; init; }
    public string? DraftContentHash { get; init; }
}

public sealed record McpGenerationMetadata(
    string? Model,
    string Mode,
    string? UserBrief,
    string? Rationale,
    IReadOnlyList<string> Assumptions,
    double Confidence,
    long GeneratedAtUtc,
    string? DraftContentHash,
    long AcceptedAtUtc,
    bool WasEdited)
{
    internal static McpGenerationMetadata FromView(GenerationMetadataView view) =>
        new(view.Model,
            view.Mode == DraftMode.Improve ? "improve" : "create",
            view.UserBrief,
            view.Rationale,
            view.Assumptions,
            view.Confidence,
            view.GeneratedAtUtc,
            view.DraftContentHash,
            view.AcceptedAtUtc,
            view.WasEdited);
}

public sealed record McpAgentDefinition(
    string Id,
    string Name,
    string? Description,
    string Instructions,
    string? ModelProfile,
    string? ReasoningEffort,
    string Kind,
    IReadOnlyList<string> AllowedToolNames,
    IReadOnlyDictionary<string, bool> ToolApprovals,
    string? OrchestrationTopologyJson,
    bool PlaybookEnabled,
    IReadOnlyList<string> AllowedSkillIds,
    bool DefaultTemporaryChat,
    bool MemoryExtractionEnabled,
    bool DisableBaseScaffold,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    McpGenerationMetadata? GenerationMetadata)
{
    public static McpAgentDefinition FromRecord(AgentDefinitionRecord record) =>
        new(record.Id.ToString("D"),
            record.Name,
            record.Description,
            record.Instructions,
            record.ModelProfile,
            record.ReasoningEffort,
            record.Kind == AgentDefinitionKind.Orchestrator ? "orchestrator" : "single",
            record.AllowedToolNames,
            record.ToolApprovals,
            record.OrchestrationTopologyJson,
            record.PlaybookEnabled,
            (record.AllowedSkillIds ?? []).Select(static id => id.ToString("D")).ToArray(),
            record.DefaultTemporaryChat,
            record.MemoryExtractionEnabled,
            record.DisableBaseScaffold,
            record.Version,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            GenerationProvenance.FromPersistedJson(record.GenerationMetadataJson) is { } metadata
                ? McpGenerationMetadata.FromView(metadata)
                : null);
}

internal static class McpAdminWireNames
{
    public static string DownloadErrorCode(HuggingFaceDownloadFailure failure) =>
        failure switch
        {
            HuggingFaceDownloadFailure.Network => "model_download_network_error",
            HuggingFaceDownloadFailure.Gated or HuggingFaceDownloadFailure.Unauthorized => "model_source_unauthorized",
            HuggingFaceDownloadFailure.DiskFull => "insufficient_disk_space",
            HuggingFaceDownloadFailure.HashMismatch => "model_hash_mismatch",
            HuggingFaceDownloadFailure.NotFound => "model_source_not_found",
            HuggingFaceDownloadFailure.DestinationConflict => McpAdminToolFailureCodes.ModelPullConflict,
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown Hugging Face download failure.")
        };

    public static string DownloadErrorCode(string? errorCode)
    {
        if (Enum.TryParse<HuggingFaceDownloadFailure>(errorCode, ignoreCase: false, out var failure)
            && Enum.IsDefined(failure))
        {
            return DownloadErrorCode(failure);
        }

        return errorCode switch
        {
            "InsufficientStorage" => "insufficient_disk_space",
            "ModelConflict" => McpAdminToolFailureCodes.ModelPullConflict,
            "DownloadCompensationFailed" => "model_pull_compensation_failed",
            "DownloadFailed" or null or "" => McpAdminToolFailureCodes.ModelPullFailed,
            _ => McpAdminToolFailureCodes.ModelPullFailed
        };
    }

    public static string SettingsField(NodeSettingsField field) =>
        field switch
        {
            NodeSettingsField.DefaultModelName => "default_model_name",
            NodeSettingsField.ToolCapableModels => "tool_capable_models",
            NodeSettingsField.MaxMessageRequestTimeoutSeconds => "max_message_request_timeout_seconds",
            NodeSettingsField.SpeculativeDraftModelName => "speculative_draft_model_name",
            NodeSettingsField.SpeculativeMode => "speculative_mode",
            NodeSettingsField.SpeculativeDraftMaxTokens => "speculative_draft_max_tokens",
            NodeSettingsField.SpeculativeDraftGpuLayers => "speculative_draft_gpu_layers",
            NodeSettingsField.ChatCacheReuse => "chat_cache_reuse",
            NodeSettingsField.LlamaIdleTimeToLiveSeconds => "llama_idle_time_to_live_seconds",
            NodeSettingsField.KeepModelWarmModelName => "keep_model_warm_model_name",
            NodeSettingsField.LlamaMaxLoadedProcesses => "llama_max_loaded_processes",
            NodeSettingsField.KeepModelWarmIntervalSeconds => "keep_model_warm_interval_seconds",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown node-settings field.")
        };

    public static string SettingsArgument(string propertyName) =>
        propertyName switch
        {
            nameof(NodeSettingsAgenticPatch.DefaultModelName) => "default_model_name",
            nameof(NodeSettingsAgenticPatch.EnableTools) => "enable_tools",
            nameof(NodeSettingsAgenticPatch.ToolCapableModels) => "tool_capable_models",
            nameof(NodeSettingsAgenticPatch.HuggingFaceDefaultQuant) => "hugging_face_default_quant",
            nameof(NodeSettingsAgenticPatch.LlamaMaxLoadedProcesses) => "llama_max_loaded_processes",
            nameof(NodeSettingsAgenticPatch.LlamaIdleTimeToLiveSeconds) => "llama_idle_time_to_live_seconds",
            nameof(NodeSettingsAgenticPatch.KeepModelWarmEnabled) => "keep_model_warm_enabled",
            nameof(NodeSettingsAgenticPatch.KeepModelWarmModelName) => "keep_model_warm_model_name",
            nameof(NodeSettingsAgenticPatch.KeepModelWarmIntervalSeconds) => "keep_model_warm_interval_seconds",
            nameof(NodeSettingsAgenticPatch.MaxMessageRequestTimeoutSeconds) => "max_message_request_timeout_seconds",
            nameof(NodeSettingsAgenticPatch.ChatCacheReuse) => "chat_cache_reuse",
            nameof(NodeSettingsAgenticPatch.SpeculativeMode) => "speculative_mode",
            nameof(NodeSettingsAgenticPatch.SpeculativeDraftModelName) => "speculative_draft_model_name",
            nameof(NodeSettingsAgenticPatch.SpeculativeDraftMaxTokens) => "speculative_draft_max_tokens",
            nameof(NodeSettingsAgenticPatch.SpeculativeDraftGpuLayers) => "speculative_draft_gpu_layers",
            nameof(NodeSettingsAgenticPatch.RerankerModelName) => "reranker_model_name",
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unknown agentic settings property.")
        };
}

internal static class McpAdminToolResponseMapper
{
    public static McpRuntimeAcquisitionResponse ToResponse(this LlamaCppRuntimeAcquisitionStatus status) =>
        new(status.Sequence,
            status.Phase,
            status.Variant,
            status.Tag,
            status.CompletedBytes,
            status.TotalBytes,
            status.StepIndex,
            status.StepCount,
            status.SanitizedError);

    public static McpNodeSettingsUpdateResponse ToResponse(this NodeSettingsAdministrationResult result)
    {
        var fields = result.ValidationErrors.Select(static error => McpAdminWireNames.SettingsField(error.Field)).ToArray();
        if (result.Updated)
        {
            return new McpNodeSettingsUpdateResponse(true, []);
        }

        var failureCode = fields.Length == 0 ? McpAdminToolFailureCodes.ValidationFailed : $"invalid_field:{fields[0]}";
        var displayMessage = result.ValidationErrors.Count == 0
            ? "The settings update was rejected."
            : result.ValidationErrors[0].Message;
        return new McpNodeSettingsUpdateResponse(false, fields, failureCode, displayMessage);
    }
}
