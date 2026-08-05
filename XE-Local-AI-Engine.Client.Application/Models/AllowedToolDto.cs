namespace XE_Local_AI_Engine.Client.Models;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Transport DTO for allowed tool data.
/// </summary>
public sealed record AllowedToolDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required ToolLocation Location { get; init; }

    /// <summary>
    ///     Human/model-facing tool description carried from the server <c>ToolDefinition.Description</c>. Attached to
    ///     the bridged <c>AIFunction</c> so the model sees it alongside <see cref="ParameterSchema" />.
    /// </summary>
    public string? Description { get; init; }

    public string? ParameterSchema { get; init; }

    /// <summary>
    ///     When true, the tool must be gated behind an approval round-trip before it executes. All current beta
    ///     tools ship as non-approval (auto-execute); the gating layer reads this flag so a future approval flow
    ///     can opt individual tools in without changing the execution path.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    ///     The tool's risk class for the node-default tool-approval policy. Defaults to
    ///     <see cref="ToolCategory.Unknown" /> so a tool the offer provider did not categorize is treated as fail-closed
    ///     (approval-requiring) by the node policy. This travels alongside the offer for policy evaluation; it is NOT part
    ///     of the runtime-package config hash (the hash keys on <c>Name</c>/<c>Location</c>/schema/<c>RequiresApproval</c>
    ///     via <c>MixedEnvelopeAllowedToolDto</c>, which is deliberately left unchanged).
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.Unknown;
}
