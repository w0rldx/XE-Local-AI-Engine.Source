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
    ///     The outcome is <see cref="PlaybookEvalOutcome.Recorded" /> with the persisted result, or
    ///     <see cref="PlaybookEvalOutcome.NotPending" /> when the action is not, or stopped being, a pending suggestion.
    /// </remarks>
    /// <exception cref="Agents.PlaybookActionNotFoundException">No action of <paramref name="agentId" /> has <paramref name="actionId" />.</exception>
    /// <exception cref="Agents.AgentDefinitionNotFoundException">The owning agent is gone.</exception>
    Task<PlaybookEvalOutcome> RunEvalAsync(Guid agentId, Guid actionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of a <see cref="IPlaybookEvalService.RunEvalAsync" /> call.
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
