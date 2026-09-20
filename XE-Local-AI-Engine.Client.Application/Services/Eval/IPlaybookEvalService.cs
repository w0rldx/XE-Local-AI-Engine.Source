namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Golden-conversation evaluation orchestration, offline and batch, never on the chat streaming path.
/// </summary>
/// <remarks>
///     It re-runs the real agent loop over the golden set with the candidate playbook prompt against the current
///     baseline, scores each case by assertion or node-local judge, and persists a plaintext
///     <see cref="PlaybookEvalResult" /> on the pending Suggested action, so the promote gate can decide.
/// </remarks>
public interface IPlaybookEvalService
{
    /// <summary>
    ///     Runs the eval for the pending action <paramref name="actionId" /> owned by <paramref name="agentId" /> and
    ///     records the result on it.
    /// </summary>
    /// <remarks>
    ///     <see cref="PlaybookEvalOutcome.ActionFound" /> is <c>false</c>, which the endpoint 404s, when the action is
    ///     missing, belongs to another agent, is not a pending Suggested or Analysis action, or its owning agent is
    ///     gone; otherwise <see cref="PlaybookEvalOutcome.Result" /> carries the persisted eval result.
    /// </remarks>
    Task<PlaybookEvalOutcome> RunEvalAsync(Guid agentId, Guid actionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of a <see cref="IPlaybookEvalService.RunEvalAsync" /> call.
/// </summary>
/// <remarks>
///     <see cref="ActionFound" /> distinguishes a 404 from a recorded eval, <see cref="Result" /> carries the
///     persisted result when one was produced, and <see cref="Action" /> carries the updated action record, now
///     bearing that result, so the endpoint maps the response directly rather than re-fetching unscoped.
/// </remarks>
public sealed class PlaybookEvalOutcome
{
    public required bool ActionFound { get; init; }

    public required PlaybookEvalResult? Result { get; init; }

    public PlaybookActionRecord? Action { get; init; }
}
