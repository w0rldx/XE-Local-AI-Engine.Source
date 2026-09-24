namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Resolves a stable weight-IDENTITY token for the configured eval model, so the playbook eval fingerprint can
///     invalidate when a model is swapped under the SAME name.
/// </summary>
/// <remarks>
///     Without an identity the fingerprint keys only on the model NAME, so a same-name re-pull leaves a recorded pass
///     trusted against DIFFERENT weights — the digest-vs-name staleness trap the classification cache avoids too.
///     The token is opaque and only ever compared for equality; its whole job is to differ when the weights do.
///     Resolution prefers the strongest source for the runtime that serves the model and NEVER throws: an
///     unresolvable identity degrades to <see cref="EvalModelIdentity.Unverified" />, never to trusting a bare name.
/// </remarks>
public interface IEvalModelIdentityResolver
{
    /// <summary>
    ///     Resolves the weight identity for <paramref name="modelName" />, verified when a real one was found and the
    ///     explicit <see cref="EvalModelIdentity.Unverified" /> sentinel otherwise.
    /// </summary>
    /// <remarks>
    ///     No identity source resolves for a blank name, a model installed under that name on no known runtime, or a
    ///     run where every lookup failed.
    /// </remarks>
    Task<EvalModelIdentity> ResolveAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>
///     The resolved weight identity for an eval model, whose <see cref="Token" /> folds into the eval fingerprint.
/// </summary>
/// <remarks>
///     <see cref="IsVerified" /> distinguishes a real weight-derived identity from the <see cref="Unverified" />
///     sentinel, so callers and logs can say "model identity unverifiable" rather than trust the fallback.
/// </remarks>
public sealed class EvalModelIdentity
{
    /// <summary>
    ///     The opaque identity token folded into the fingerprint.
    /// </summary>
    /// <remarks>
    ///     A verified token carries a source prefix such as <c>gguf-sha256:</c> or <c>ollama-digest:</c>, while the
    ///     sentinel <see cref="UnverifiedToken" /> shares no prefix with any of them, so an unverified run can never
    ///     collide with a verified one of the same name.
    /// </remarks>
    public required string Token { get; init; }

    /// <summary>Whether a real weight identity was resolved (as opposed to the unverified fallback).</summary>
    public required bool IsVerified { get; init; }

    /// <summary>
    ///     The sentinel token recorded when no weight identity could be resolved. Deliberately prefix-free so it never
    ///     equals a verified token, keeping an identity-unverifiable run distinct from a verified run of the same name.
    /// </summary>
    public const string UnverifiedToken = "unverified";

    /// <summary>The shared unverified identity (the <see cref="UnverifiedToken" /> sentinel, not verified).</summary>
    public static EvalModelIdentity Unverified { get; } = new()
    {
        Token = UnverifiedToken,
        IsVerified = false
    };
}
