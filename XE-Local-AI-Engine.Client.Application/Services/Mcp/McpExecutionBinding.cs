namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.Client.Models;

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

    public McpInboundExecutionContext InboundContext { get; init; } = McpInboundExecutionContext.Delegate;

    public Guid ExecutionRequestId { get; init; }
}

/// <summary>
///     A complete, in-memory snapshot of the model-visible configuration for one inbound run. The keyed fingerprint is
///     safe to persist and compare; the remaining fields are execution inputs and must not be persisted as plaintext.
/// </summary>
public sealed record McpExecutionBinding
{
    public required string BindingFingerprint { get; init; }

    public required string ModelId { get; init; }

    public required string Instructions { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required int? AgentDefinitionVersion { get; init; }

    public required IReadOnlyList<AllowedToolDto> AllowedTools { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required bool SupportsThinking { get; init; }

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for <see cref="ModelId" />, which requires its chat
    ///     template to render a literal reasoning end marker.
    /// </summary>
    /// <remarks>
    ///     Gates the budget marker on the child agent's construction-time options exactly as <see cref="SupportsThinking" /> gates the
    ///     <c>think</c> field. Deliberately NOT part of the keyed binding fingerprint: it is derived from <see cref="ModelId" />, which
    ///     already participates, so folding it in would only invalidate every previously recorded fingerprint. It defaults to
    ///     <see langword="true" />, the value that never removes a working cap.
    /// </remarks>
    public bool ReasoningBudgetEnforceable { get; init; } = true;
}

/// <summary>A non-throwing binding resolution result suitable for an external, unattended caller.</summary>
public sealed class McpExecutionBindingResolution
{
    public required McpExecutionBinding? Binding { get; init; }

    public required string? FailureCode { get; init; }

    public required string DisplayMessage { get; init; }

    public bool IsSuccess => Binding is not null;

    public static McpExecutionBindingResolution Success(McpExecutionBinding binding) =>
        new()
        {
            Binding = binding,
            FailureCode = null,
            DisplayMessage = string.Empty
        };

    public static McpExecutionBindingResolution Rejected(string failureCode, string displayMessage) =>
        new()
        {
            Binding = null,
            FailureCode = failureCode,
            DisplayMessage = displayMessage
        };
}
