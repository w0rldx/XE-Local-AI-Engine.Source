namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>Stable failure codes returned by the inbound MCP execution boundary.</summary>
public static class McpExecutionFailureCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string AgentNotFound = "agent_not_found";
    public const string AgentConfigChanged = "agent_config_changed";
    public const string ModelNotAvailable = "model_not_available";
    public const string ModelOverrideNotAllowed = "model_override_not_allowed";
    public const string CapacityDeclined = "capacity_declined";
    public const string Cancelled = "cancelled";
    public const string InternalFailure = "internal_failure";
    public const string WorkspaceNotAuthorized = "workspace_not_authorized";
    public const string WorkspaceBusy = "workspace_busy";
    public const string WorkspacePreparationFailed = "workspace_preparation_failed";
}

/// <summary>
///     Inputs which select the immutable execution binding for an inbound MCP run. A model override is accepted only
///     for a saved definition which does not pin its own model.
/// </summary>
public sealed record McpExecutionBindingRequest
{
    public string? AgentKey { get; init; }

    public string? ModelId { get; init; }

    public string? ModelOverrideId { get; init; }

    public string? Instructions { get; init; }
}

/// <summary>
///     A complete, in-memory snapshot of the model-visible configuration for one inbound run. The keyed fingerprint is
///     safe to persist and compare; the remaining fields are execution inputs and must not be persisted as plaintext.
/// </summary>
public sealed record McpExecutionBinding(
    string BindingFingerprint,
    string ModelId,
    string Instructions,
    Guid? AgentDefinitionId,
    int? AgentDefinitionVersion,
    IReadOnlyList<AllowedToolDto> AllowedTools,
    string? ReasoningEffort,
    bool SupportsThinking);

/// <summary>Shared fail-closed policy for the only binding allowed to receive an opaque workspace.</summary>
internal static class McpExecutionBindingPolicy
{
    private static readonly HashSet<string> WorkspaceToolNames =
    [
        "list_files",
        "read_file",
        "search_text"
    ];

    public static bool IsExactReadOnlyWorkspaceCoder(McpExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.AgentDefinitionId is not null
               && binding.AllowedTools.Count == WorkspaceToolNames.Count
               && binding.AllowedTools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() == WorkspaceToolNames.Count
               && binding.AllowedTools.All(static tool => WorkspaceToolNames.Contains(tool.Name)
                                                          && tool.Location == ToolLocation.ClientLocal
                                                          && tool.Category == ToolCategory.ReadLocal
                                                          && !tool.RequiresApproval);
    }
}

/// <summary>A non-throwing binding resolution result suitable for an external, unattended caller.</summary>
public sealed record McpExecutionBindingResolution(McpExecutionBinding? Binding, string? FailureCode, string DisplayMessage)
{
    public bool IsSuccess => Binding is not null;

    public static McpExecutionBindingResolution Success(McpExecutionBinding binding) =>
        new(binding, FailureCode: null, string.Empty);

    public static McpExecutionBindingResolution Rejected(string failureCode, string displayMessage) =>
        new(Binding: null, failureCode, displayMessage);
}
