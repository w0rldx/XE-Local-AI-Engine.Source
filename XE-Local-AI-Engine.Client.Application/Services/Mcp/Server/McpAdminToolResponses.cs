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

    /// <summary>What a tool over a switched-off feature answers. A disabled node says so; it does not fault.</summary>
    public const string NotAvailable = "not_available";

    public const string InvalidStatus = "invalid_status";
    public const string RunNotFound = "run_not_found";

    /// <summary>A write refused because the stored record kept changing under it. Nothing sent was wrong; retry.</summary>
    public const string SettingsConflict = "settings_conflict";

    public const string ValidationFailed = "validation_failed";
}

public sealed class McpNodeStatusResponse
{
    public required string Version { get; init; }

    public required long UptimeSeconds { get; init; }

    public required string? DefaultModelName { get; init; }

    public required int LoadedProcessCount { get; init; }
}

public sealed class McpRuntimeStatusResponse
{
    public required string? InstalledTag { get; init; }

    public required string RecommendedTag { get; init; }

    public required string? UpstreamLatestTag { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required bool IsOffline { get; init; }

    public required int LoadedProcessCount { get; init; }
}

public sealed class McpRuntimeAcquisitionStartResponse
{
    public required string Status { get; init; }

    public required string? Variant { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpRuntimeAcquisitionResponse
{
    public required long Sequence { get; init; }

    public required string Phase { get; init; }

    public required string? Variant { get; init; }

    public required string? Tag { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required int StepIndex { get; init; }

    public required int StepCount { get; init; }

    public required string? SanitizedError { get; init; }
}

public sealed class McpModelPullStartResponse
{
    public required string Status { get; init; }

    public required string? ModelName { get; init; }

    public required string? OperationId { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpModelPullResponse
{
    public required string Status { get; init; }

    public required string? ModelName { get; init; }

    public required string? Phase { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required string? SanitizedError { get; init; }

    public required string? OperationId { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpModelPullCancelResponse
{
    public required bool Cancelled { get; init; }
}

public sealed class McpModelDeleteResponse
{
    public required bool Deleted { get; init; }

    public required string? ModelName { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpDefaultModelResponse
{
    public required bool Updated { get; init; }

    public required string? SelectedModelName { get; init; }

    public required string? PreviousDefault { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpNodeSettingsUpdateResponse
{
    public required bool Updated { get; init; }

    public required IReadOnlyList<string> RejectedFields { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpAgentResponse
{
    public required string Status { get; init; }

    public required McpAgentDefinition? Agent { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class McpAgentDeleteResponse
{
    public required bool Deleted { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

/// <summary>
///     One development-workflow run as an observing agent sees it: lifecycle metadata and node tallies, nothing else.
/// </summary>
/// <remarks>
///     The pinned graph, artifact bytes, work-session transcripts and every host path stay on the REST surface a
///     browser operator uses: an MCP client is told how a run is going, not handed its contents.
/// </remarks>
public record McpWorkflowRunSummary(
    string RunId,
    string WorkItemId,
    string? DefinitionName,
    string Status,
    int QueuedNodeCount,
    int RunningNodeCount,
    int CompletedNodeCount,
    int TotalNodeCount,
    int PendingDecisionCount);

/// <summary>Bounded run listing: one row per work item's latest run, matching the one existing REST list surface.</summary>
public sealed class McpWorkflowRunListResponse
{
    public required string Status { get; init; }

    public required IReadOnlyList<McpWorkflowRunSummary> Runs { get; init; }

    public required int Count { get; init; }

    public required int Limit { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

/// <summary>One node run, reduced to the five fields that answer where a run stands and how hard a node tried.</summary>
public sealed class McpWorkflowNodeRunSummary
{
    public required string NodeKey { get; init; }

    public required string NodeType { get; init; }

    public required string Status { get; init; }

    public required int Attempt { get; init; }

    public required int MaxAttempts { get; init; }
}

/// <summary>
///     A single run's observation: the list row's own fields, why it ended if it has, and its node rows, FLAT, so a
///     caller reads <c>run.status</c> rather than <c>run.run.status</c>.
/// </summary>
/// <remarks>
///     It extends the summary rather than embedding one, which keeps the field set a caller sees here identical to the
///     one the listing returns.
/// </remarks>
public sealed record McpWorkflowRunDetail(
    string RunId,
    string WorkItemId,
    string? DefinitionName,
    string Status,
    int QueuedNodeCount,
    int RunningNodeCount,
    int CompletedNodeCount,
    int TotalNodeCount,
    int PendingDecisionCount,
    string? FailureClass,
    string? TerminalReason,
    long? StartedAtUtc,
    long? EndedAtUtc,
    IReadOnlyList<McpWorkflowNodeRunSummary> Nodes) : McpWorkflowRunSummary(RunId,
    WorkItemId,
    DefinitionName,
    Status,
    QueuedNodeCount,
    RunningNodeCount,
    CompletedNodeCount,
    TotalNodeCount,
    PendingDecisionCount);

public sealed class McpWorkflowRunGetResponse
{
    public required string Status { get; init; }

    public required McpWorkflowRunDetail? Run { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

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

public sealed class McpGenerationMetadata
{
    public required string? Model { get; init; }

    public required string Mode { get; init; }

    public required string? UserBrief { get; init; }

    public required string? Rationale { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }

    public required double Confidence { get; init; }

    public required long GeneratedAtUtc { get; init; }

    public required string? DraftContentHash { get; init; }

    public required long AcceptedAtUtc { get; init; }

    public required bool WasEdited { get; init; }

    internal static McpGenerationMetadata FromView(GenerationMetadataView view) =>
        new()
        {
            Model = view.Model,
            Mode = view.Mode == DraftMode.Improve ? "improve" : "create",
            UserBrief = view.UserBrief,
            Rationale = view.Rationale,
            Assumptions = view.Assumptions,
            Confidence = view.Confidence,
            GeneratedAtUtc = view.GeneratedAtUtc,
            DraftContentHash = view.DraftContentHash,
            AcceptedAtUtc = view.AcceptedAtUtc,
            WasEdited = view.WasEdited
        };
}

public sealed class McpAgentDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string Instructions { get; init; }

    public required string? ModelProfile { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required string Kind { get; init; }

    public required IReadOnlyList<string> AllowedToolNames { get; init; }

    public required IReadOnlyDictionary<string, bool> ToolApprovals { get; init; }

    public required string? OrchestrationTopologyJson { get; init; }

    public required bool PlaybookEnabled { get; init; }

    public required IReadOnlyList<string> AllowedSkillIds { get; init; }

    public required bool DefaultTemporaryChat { get; init; }

    public required bool MemoryExtractionEnabled { get; init; }

    public required bool DisableBaseScaffold { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required McpGenerationMetadata? GenerationMetadata { get; init; }

    public static McpAgentDefinition FromRecord(AgentDefinitionRecord record) =>
        new()
        {
            Id = record.Id.ToString("D"),
            Name = record.Name,
            Description = record.Description,
            Instructions = record.Instructions,
            ModelProfile = record.ModelProfile,
            ReasoningEffort = record.ReasoningEffort,
            Kind = record.Kind == AgentDefinitionKind.Orchestrator ? "orchestrator" : "single",
            AllowedToolNames = record.AllowedToolNames,
            ToolApprovals = record.ToolApprovals,
            OrchestrationTopologyJson = record.OrchestrationTopologyJson,
            PlaybookEnabled = record.PlaybookEnabled,
            AllowedSkillIds = (record.AllowedSkillIds ?? []).Select(static id => id.ToString("D")).ToArray(),
            DefaultTemporaryChat = record.DefaultTemporaryChat,
            MemoryExtractionEnabled = record.MemoryExtractionEnabled,
            DisableBaseScaffold = record.DisableBaseScaffold,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            GenerationMetadata = GenerationProvenance.FromPersistedJson(record.GenerationMetadataJson) is { } metadata
                ? McpGenerationMetadata.FromView(metadata)
                : null
        };
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
            NodeSettingsField.KvCacheType => "kv_cache_type",
            NodeSettingsField.ChatCacheReuse => "chat_cache_reuse",
            NodeSettingsField.LlamaIdleTimeToLiveSeconds => "llama_idle_time_to_live_seconds",
            NodeSettingsField.KeepModelWarmModelName => "keep_model_warm_model_name",
            NodeSettingsField.LlamaMaxLoadedProcesses => "llama_max_loaded_processes",
            NodeSettingsField.KeepModelWarmIntervalSeconds => "keep_model_warm_interval_seconds",
            NodeSettingsField.AutoEffortFastModelName => "auto_effort_fast_model_name",
            NodeSettingsField.ContainerRuntimeSelection => "container_runtime_selection",
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
            nameof(NodeSettingsAgenticPatch.KvCacheType) => "kv_cache_type",
            nameof(NodeSettingsAgenticPatch.RerankerModelName) => "reranker_model_name",
            nameof(NodeSettingsAgenticPatch.AutoEffortFastModelName) => "auto_effort_fast_model_name",
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unknown agentic settings property.")
        };
}

internal static class McpAdminToolResponseMapper
{
    public static McpRuntimeAcquisitionResponse ToResponse(this LlamaCppRuntimeAcquisitionStatus status) =>
        new()
        {
            Sequence = status.Sequence,
            Phase = status.Phase,
            Variant = status.Variant,
            Tag = status.Tag,
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            StepIndex = status.StepIndex,
            StepCount = status.StepCount,
            SanitizedError = status.SanitizedError
        };

    public static McpNodeSettingsUpdateResponse ToResponse(this NodeSettingsAdministrationResult result)
    {
        var fields = result.ValidationErrors.Select(static error => McpAdminWireNames.SettingsField(error.Field)).ToArray();
        if (result.Updated)
        {
            return new McpNodeSettingsUpdateResponse { Updated = true, RejectedFields = [] };
        }

        // A conflict is not a rejection: it names no field, and the caller's own patch was valid. Told apart here so a
        // tool-using agent retries instead of "correcting" a field that was never the problem.
        if (result.Conflicted)
        {
            return new McpNodeSettingsUpdateResponse
            {
                Updated = false,
                RejectedFields = [],
                FailureCode = McpAdminToolFailureCodes.SettingsConflict,
                DisplayMessage = "Node settings changed while this update was being validated. Nothing was written; read the settings again and retry."
            };
        }

        var failureCode = fields.Length == 0 ? McpAdminToolFailureCodes.ValidationFailed : $"invalid_field:{fields[0]}";
        var displayMessage = result.ValidationErrors.Count == 0
            ? "The settings update was rejected."
            : result.ValidationErrors[0].Message;
        return new McpNodeSettingsUpdateResponse { Updated = false, RejectedFields = fields, FailureCode = failureCode, DisplayMessage = displayMessage };
    }
}
