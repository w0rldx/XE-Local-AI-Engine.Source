namespace XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Why a draft could not be produced, for the states the UI treats specially. Bad input (oversized field, prompt
///     budget, ineligible model) is a plain 400 through the endpoint's validation errors instead.
/// </summary>
public enum DraftErrorCode
{
    /// <summary>The node is running an invocation or another draft; drafts never queue. Rendered as a notice, not an error.</summary>
    NodeBusy = 0,

    /// <summary>The model returned nothing usable, or the generation budget elapsed. Rendered as "try again / different model".</summary>
    Unparseable = 1
}

/// <summary>
///     Typed failure body for the draft endpoints. <see cref="Message" /> is a fixed operator-facing string composed by
///     the drafting service — raw model output is never echoed into it.
/// </summary>
public sealed class DraftErrorResponse
{
    public required DraftErrorCode Code { get; init; }

    public required string Message { get; init; }
}
