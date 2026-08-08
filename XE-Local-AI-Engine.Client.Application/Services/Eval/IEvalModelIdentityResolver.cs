namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Resolves a stable weight-IDENTITY token for the configured eval model so the playbook eval fingerprint can
///     invalidate when a model is swapped under the SAME name (an Ollama / llama.cpp weight swap). Without an identity,
///     the fingerprint keys only on the model NAME, so a same-name re-download / re-pull leaves a recorded eval pass
///     trusted against DIFFERENT weights — the same digest-vs-name staleness trap the model-kind classification cache
///     avoids by keying on content digest.
/// </summary>
/// <remarks>
///     The token is opaque and only ever compared for equality; its whole job is to differ when the underlying weights
///     differ. Resolution prefers the strongest source available for the runtime that serves the model and NEVER throws
///     — an unresolvable identity degrades to <see cref="EvalModelIdentity.Unverified" /> rather than silently trusting
///     the bare name.
/// </remarks>
public interface IEvalModelIdentityResolver
{
    /// <summary>
    ///     Resolves the weight identity for <paramref name="modelName" />. Returns a
    ///     <see cref="EvalModelIdentity.IsVerified" /> identity when a real weight identity was found, or
    ///     <see cref="EvalModelIdentity.Unverified" /> (the explicit sentinel) when no identity source could be resolved
    ///     (a blank name, the model is not installed under that name on any known runtime, or every lookup failed).
    /// </summary>
    Task<EvalModelIdentity> ResolveAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>
///     The resolved weight identity for an eval model. <see cref="Token" /> is folded into the eval fingerprint;
///     <see cref="IsVerified" /> distinguishes a real, weight-derived identity from the <see cref="Unverified" />
///     sentinel so callers (and logs) can surface "model identity unverifiable" rather than treating the fallback as a
///     trusted identity.
/// </summary>
/// <param name="Token">
///     The opaque identity token folded into the fingerprint. A verified token carries a source prefix
///     (e.g. <c>gguf-sha256:</c>, <c>gguf-rev:</c>, <c>ollama-digest:</c>); the unverified sentinel is
///     <see cref="UnverifiedToken" />, which shares no prefix with any verified token, so an unverified run can never
///     collide with a verified one of the same name.
/// </param>
/// <param name="IsVerified">Whether a real weight identity was resolved (as opposed to the unverified fallback).</param>
public sealed record EvalModelIdentity(string Token, bool IsVerified)
{
    /// <summary>
    ///     The sentinel token recorded when no weight identity could be resolved. Deliberately prefix-free so it never
    ///     equals a verified token, keeping an identity-unverifiable run distinct from a verified run of the same name.
    /// </summary>
    public const string UnverifiedToken = "unverified";

    /// <summary>The shared unverified identity (the <see cref="UnverifiedToken" /> sentinel, not verified).</summary>
    public static EvalModelIdentity Unverified { get; } = new(UnverifiedToken, IsVerified: false);
}
