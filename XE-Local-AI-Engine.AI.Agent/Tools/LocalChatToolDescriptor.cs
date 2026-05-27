namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
/// Offer-list metadata for a single local-chat tool. Carries the public surface the send path needs to build a
/// matching transport DTO (name + description + JSON schema + approval flag) WITHOUT exposing the executable
/// <see cref="Microsoft.Extensions.AI.AIFunction"/>. The schema is derived from the function's generated schema,
/// never hand-written, so the offered contract stays in lock-step with what the factory actually executes.
/// </summary>
internal sealed record LocalChatToolDescriptor(
    string Name,
    string Description,
    string? ParameterSchema,
    bool RequiresApproval);
