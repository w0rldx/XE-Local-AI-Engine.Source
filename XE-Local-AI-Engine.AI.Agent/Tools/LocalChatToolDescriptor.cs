namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Offer-list metadata for a single local-chat tool. Carries the public surface the send path needs to build a
///     matching transport DTO (name + description + JSON schema + approval flag + risk category) WITHOUT exposing the
///     executable <see cref="Microsoft.Extensions.AI.AIFunction" />. The schema is derived from the function's generated
///     schema, never hand-written, so the offered contract stays in lock-step with what the factory actually executes.
///     <para>
///         <see cref="Category" /> is the tool's risk class for the node-default approval policy. It is an
///         optional trailing parameter defaulting to <see cref="ToolCategory.Unknown" /> so existing four-argument call
///         sites keep compiling; a descriptor that leaves it unset is treated as fail-closed (approval-requiring) by the
///         node policy. Each real definition site declares its own category.
///     </para>
///     <para>
///         <see cref="IsFixedCustomTool" /> is set only by the node-local custom-tool catalog and records whether that
///         tool runs a verbatim, operator-authored invocation (<c>CustomToolMode.Fixed</c>) rather than one the model
///         parameterizes. It is carried here — rather than re-read from the store — because it is the one bit the node
///         tool-catalog response needs to tell an operator whether "approve for this session" can be honored for the
///         tool (see <c>SessionApprovalEligibility</c>). It is <see langword="false" /> for every non-custom tool.
///     </para>
/// </summary>
internal sealed record LocalChatToolDescriptor(
    string Name,
    string Description,
    string? ParameterSchema,
    bool RequiresApproval,
    ToolCategory Category = ToolCategory.Unknown,
    bool IsFixedCustomTool = false);
