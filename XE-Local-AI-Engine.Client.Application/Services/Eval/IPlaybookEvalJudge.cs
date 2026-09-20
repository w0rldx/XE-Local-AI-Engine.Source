namespace XE_Local_AI_Engine.Client.Services.Eval;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Scores a single candidate or baseline agent output against one golden case, deterministically or with a model.
/// </summary>
/// <remarks>
///     The <em>assertion</em> path runs in plain code with no model call, while the <em>judge</em> path uses the
///     case's rubric and the SUPPLIED node-local chat client: the service owns the per-run client and passes it in,
///     so the judge never resolves a shared or cloud one. A case with neither an assertion nor a rubric is invalid
///     and scores as a fail — defence in depth, since the CRUD service rejects such a case on create.
/// </remarks>
public interface IPlaybookEvalJudge
{
    /// <summary>
    ///     Scores <paramref name="candidateText" /> against <paramref name="goldenCase" />. Uses the deterministic
    ///     assertion path when the case carries an <c>Assertion</c>, otherwise the node-local judge path with
    ///     <paramref name="nodeLocalClient" />.
    /// </summary>
    Task<EvalScore> ScoreAsync(GoldenConversationRecord goldenCase,
        string candidateText,
        IChatClient nodeLocalClient,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The outcome of scoring one golden case: <see cref="Pass" /> is the pass/fail verdict, <see cref="ScoredBy" />
///     records which path decided it (<c>"assertion"</c> or <c>"judge"</c>) for the persisted audit.
/// </summary>
public readonly record struct EvalScore(bool Pass, string ScoredBy);
