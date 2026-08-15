namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Request for <c>POST skills/draft</c>. In <see cref="DraftMode.Create" /> only <see cref="ModelName" /> and
///     <see cref="Brief" /> are read; in <see cref="DraftMode.Improve" /> the three <c>Existing*</c> fields carry the
///     content being revised (<see cref="ExistingContent" /> is the SKILL.md body). Every field is capped at the
///     endpoint before the drafting service takes the single draft slot.
/// </summary>
public sealed class DraftSkillRequest
{
    public DraftMode Mode { get; init; }

    /// <summary>The node-local chat model to draft with. Verified server-side against a fail-closed eligibility check.</summary>
    public string? ModelName { get; init; }

    /// <summary>The operator's description of what they want. At most 4000 characters.</summary>
    public string? Brief { get; init; }

    /// <summary>Improve mode only: the current skill name. At most 64 characters.</summary>
    public string? ExistingName { get; init; }

    /// <summary>Improve mode only: the current description. At most 1024 characters.</summary>
    public string? ExistingDescription { get; init; }

    /// <summary>Improve mode only: the current SKILL.md body. At most 20000 characters.</summary>
    public string? ExistingContent { get; init; }
}

/// <summary>
///     A drafted skill. Nothing here is persisted. Saving this content through the create/update routes with
///     <c>generated: true</c> lands it in the Imported posture (disabled, fenced) regardless of what the client asks
///     for; <see cref="GenerationMetadata" /> is echoed back unchanged on that save.
/// </summary>
public sealed class SkillDraftResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Body { get; init; }

    public required GenerationMetadata GenerationMetadata { get; init; }
}
