namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using XE_Local_AI_Engine.Client.Services.Drafting;

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

/// <summary>
///     Shared boundary checks for the two draft endpoints. Every request field is capped here, before the drafting
///     service acquires the single draft slot, so an oversized or hostile request can never hold it (invariant 7). The
///     service re-checks an aggregate prompt budget as the belt behind this brace.
/// </summary>
internal static class DraftEndpointSupport
{
    internal const int MaxBriefLength = 4000;
    internal const int MaxExistingContentLength = 20000;
    internal const int MaxModelNameLength = 200;

    /// <summary>
    ///     Returns an operator-facing message when a field is missing or over its cap, or <c>null</c> when the request
    ///     is acceptable. The two name/description caps differ per surface (agent vs. skill), so they are passed in.
    /// </summary>
    public static string? ValidateRequest(string? modelName,
        string? brief,
        string? existingName,
        string? existingDescription,
        string? existingContent,
        int maxExistingNameLength,
        int maxExistingDescriptionLength)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return "A model is required.";
        }

        if (modelName.Length > MaxModelNameLength)
        {
            return $"Model must be at most {MaxModelNameLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(brief))
        {
            return "A description of what you want is required.";
        }

        if (brief.Length > MaxBriefLength)
        {
            return $"Description must be at most {MaxBriefLength} characters.";
        }

        if (existingName is { } name && name.Length > maxExistingNameLength)
        {
            return $"Existing name must be at most {maxExistingNameLength} characters.";
        }

        if (existingDescription is { } description && description.Length > maxExistingDescriptionLength)
        {
            return $"Existing description must be at most {maxExistingDescriptionLength} characters.";
        }

        if (existingContent is { } content && content.Length > MaxExistingContentLength)
        {
            return $"Existing content must be at most {MaxExistingContentLength} characters.";
        }

        return null;
    }

    /// <summary>
    ///     Maps a failed <see cref="DraftResult" /> to its typed 409/422 response, or <c>null</c> for the kinds that are
    ///     plain input rejections and belong on the endpoint's 400 validation-error path.
    /// </summary>
    public static IResult? ToTypedFailure(DraftResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var message = result.FailureMessage ?? "The draft could not be produced.";

        return result.Failure switch
        {
            DraftFailureKind.NodeBusy => Results.Conflict(new DraftErrorResponse
            {
                Code = DraftErrorCode.NodeBusy,
                Message = message
            }),
            DraftFailureKind.Unparseable => Results.UnprocessableEntity(new DraftErrorResponse
            {
                Code = DraftErrorCode.Unparseable,
                Message = message
            }),
            // ModelNotEligible and InvalidRequest are both "your request was rejected" — 400, via AddError.
            _ => null
        };
    }
}
