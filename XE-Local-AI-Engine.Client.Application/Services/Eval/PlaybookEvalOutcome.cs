namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Result of a <see cref="Implementation.PlaybookEvalService.RunEvalAsync" /> call.
/// </summary>
/// <remarks>
///     <see cref="Recorded" /> carries the persisted result and the updated action record, now bearing that result, so
///     the endpoint maps the response directly rather than re-fetching unscoped.
/// </remarks>
public abstract record PlaybookEvalOutcome
{
    public sealed record Recorded : PlaybookEvalOutcome
    {
        public required PlaybookEvalResult Result { get; init; }

        public required PlaybookActionRecord Action { get; init; }
    }

    /// <summary>The action exists but is not a pending suggestion, so nothing ran or the result was not recorded.</summary>
    public sealed record NotPending : PlaybookEvalOutcome;
}
