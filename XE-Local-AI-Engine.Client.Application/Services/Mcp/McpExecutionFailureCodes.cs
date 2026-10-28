namespace XE_Local_AI_Engine.Client.Services.Mcp;

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

    /// <summary>The run exceeded the node's "Maximum message request timeout" — distinct from an operator cancel.</summary>
    public const string TimedOut = "timed_out";

    public const string InternalFailure = "internal_failure";
    public const string WorkspaceNotAuthorized = "workspace_not_authorized";
    public const string WorkspaceBusy = "workspace_busy";
    public const string WorkspacePreparationFailed = "workspace_preparation_failed";

    /// <summary>The run finished without any answer text.</summary>
    public const string NoAnswer = "no_answer";

    /// <summary>The run stopped on a tool approval request that nothing on the inbound path can answer.</summary>
    public const string ApprovalRequired = "approval_required";

    /// <summary>The task does not fit the bound (<c>MaxTaskUtf8Bytes</c>) or the model's context window.</summary>
    public const string TaskTooLarge = "task_too_large";

    /// <summary>The run hit the node's provider-call or cumulative input-token ceiling.</summary>
    public const string BudgetExceeded = "budget_exceeded";

    /// <summary>Several agents share the requested name; name-keyed lookups refuse to pick one.</summary>
    public const string AmbiguousName = "ambiguous_name";
}
