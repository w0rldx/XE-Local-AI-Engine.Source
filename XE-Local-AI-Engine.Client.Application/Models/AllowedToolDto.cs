namespace XE_Local_AI_Engine.Client.Models;

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
}
