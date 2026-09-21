namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Offer-list metadata for a single local-chat tool: the public surface the send path needs to build a matching
///     transport DTO, WITHOUT exposing the executable <see cref="Microsoft.Extensions.AI.AIFunction" />.
/// </summary>
/// <remarks>
///     The schema is derived from the function's generated schema, never hand-written, so the offered contract cannot
///     drift from what the factory executes. <see cref="Category" /> defaults to <see cref="ToolCategory.Unknown" />,
///     which the node policy treats as fail-closed, and each real definition site declares its own.
///     <see cref="IsFixedCustomTool" /> is <see langword="false" /> for every non-custom tool. See
///     docs/wiki/04-agent-mode.md ("The risk taxonomy (`ToolCategory`)").
/// </remarks>
internal sealed class LocalChatToolDescriptor
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string? ParameterSchema { get; init; }

    public required bool RequiresApproval { get; init; }

    public ToolCategory Category { get; init; } = ToolCategory.Unknown;

    public bool IsFixedCustomTool { get; init; }
}
