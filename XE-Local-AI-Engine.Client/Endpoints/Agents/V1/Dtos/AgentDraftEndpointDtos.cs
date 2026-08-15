namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Request for <c>POST agents/draft</c>. In <see cref="DraftMode.Create" /> only <see cref="ModelName" /> and
///     <see cref="Brief" /> are read; in <see cref="DraftMode.Improve" /> the three <c>Existing*</c> fields carry the
///     content being revised (<see cref="ExistingContent" /> is the agent's instructions). Every field is capped at the
///     endpoint before the drafting service takes the single draft slot.
/// </summary>
public sealed class DraftAgentDefinitionRequest
{
    public DraftMode Mode { get; init; }

    /// <summary>The node-local chat model to draft with. Verified server-side against a fail-closed eligibility check.</summary>
    public string? ModelName { get; init; }

    /// <summary>The operator's description of what they want. At most 4000 characters.</summary>
    public string? Brief { get; init; }

    /// <summary>Improve mode only: the current name. At most 120 characters.</summary>
    public string? ExistingName { get; init; }

    /// <summary>Improve mode only: the current description. At most 2000 characters.</summary>
    public string? ExistingDescription { get; init; }

    /// <summary>Improve mode only: the current instructions. At most 20000 characters.</summary>
    public string? ExistingContent { get; init; }
}

/// <summary>
///     A drafted agent definition. Nothing here is persisted: the fields populate the operator's form, and
///     <see cref="GenerationMetadata" /> is echoed back unchanged on the create/update request that eventually saves it
///     (see that type for why the provenance is informational rather than an attestation).
/// </summary>
public sealed class AgentDraftResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Instructions { get; init; }

    public required GenerationMetadata GenerationMetadata { get; init; }
}
