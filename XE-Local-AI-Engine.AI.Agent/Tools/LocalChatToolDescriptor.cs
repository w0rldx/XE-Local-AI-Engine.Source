namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Offer-list metadata for a single local-chat tool. Carries the public surface the send path needs to build a
///     matching transport DTO (name + description + JSON schema + approval flag + risk category) WITHOUT exposing the
///     executable <see cref="Microsoft.Extensions.AI.AIFunction" />. The schema is derived from the function's generated
///     schema, never hand-written, so the offered contract stays in lock-step with what the factory actually executes.
///     <para>
///         <see cref="Category" /> is the tool's risk class for the node-default approval policy (OPP-03). It is an
///         optional trailing parameter defaulting to <see cref="ToolCategory.Unknown" /> so existing four-argument call
///         sites keep compiling; a descriptor that leaves it unset is treated as fail-closed (approval-requiring) by the
///         node policy. Each real definition site declares its own category.
///     </para>
/// </summary>
internal sealed record LocalChatToolDescriptor(
    string Name,
    string Description,
    string? ParameterSchema,
    bool RequiresApproval,
    ToolCategory Category = ToolCategory.Unknown);
