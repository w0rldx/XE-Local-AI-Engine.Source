namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Request for <c>POST images/prompts/draft</c>: <see cref="DraftMode.Create" /> reads only <see cref="ModelName" />
///     and <see cref="Brief" />, while <see cref="DraftMode.Improve" /> also revises the current prompts.
/// </summary>
public sealed class DraftImagePromptRequest
{
    public DraftMode Mode { get; init; }

    /// <summary>The node-local chat model to draft with. Verified server-side against a fail-closed eligibility check.</summary>
    public string? ModelName { get; init; }

    /// <summary>The operator's image idea. At most 4000 characters.</summary>
    public string? Brief { get; init; }

    /// <summary>Improve mode only: the current prompt. At most 2000 characters.</summary>
    public string? ExistingPrompt { get; init; }

    /// <summary>Improve mode only: the current negative prompt. At most 2000 characters.</summary>
    public string? ExistingNegativePrompt { get; init; }
}

/// <summary>A drafted image prompt; nothing here is persisted — the fields fill the generation form.</summary>
public sealed class ImagePromptDraftResponse
{
    public required string Prompt { get; init; }

    /// <summary>Empty when the model proposed nothing to avoid.</summary>
    public required string NegativePrompt { get; init; }

    public required GenerationMetadata GenerationMetadata { get; init; }
}
