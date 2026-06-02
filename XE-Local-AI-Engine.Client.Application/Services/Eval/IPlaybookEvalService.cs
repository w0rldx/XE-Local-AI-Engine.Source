namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Golden-conversation evaluation orchestration (offline / batch — never on the chat streaming path). Re-runs the real agent
///     loop over the agent's golden conversation set with the candidate playbook prompt vs the current baseline, scores
///     each case (assertion or node-local judge), and persists a plaintext <see cref="PlaybookEvalResult" /> on the
///     pending Suggested action so the promote gate can decide whether the candidate may be enabled.
/// </summary>
public interface IPlaybookEvalService
{
    /// <summary>
    ///     Runs the eval for the pending Suggested/Analysis action <paramref name="actionId" /> owned by
    ///     <paramref name="agentId" /> and records the result on the action. Returns
    ///     <see cref="PlaybookEvalOutcome.ActionFound" /> = <c>false</c> when the action is missing, belongs to another
    ///     agent, is not a pending Suggested/Analysis action, or the owning agent is missing (the endpoint 404s); the
    ///     <see cref="PlaybookEvalOutcome.Result" /> carries the persisted eval result otherwise.
    /// </summary>
    Task<PlaybookEvalOutcome> RunEvalAsync(Guid agentId, Guid actionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of a <see cref="IPlaybookEvalService.RunEvalAsync" /> call: <see cref="ActionFound" /> distinguishes a 404
///     (no pending suggestion) from a recorded eval, <see cref="Result" /> carries the persisted eval result when one
///     was produced, and <see cref="Action" /> carries the updated action record (now bearing the recorded EvalResult)
///     so the endpoint maps the response directly — no second, unscoped re-fetch.
/// </summary>
public sealed record PlaybookEvalOutcome(bool ActionFound, PlaybookEvalResult? Result, PlaybookActionRecord? Action = null);
