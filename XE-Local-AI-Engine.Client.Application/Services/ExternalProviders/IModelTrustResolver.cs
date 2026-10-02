namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Where a model's prompts actually go, as the policy gates need to know it.
/// </summary>
/// <remarks>
///     Deliberately TRI-state: the two positive declarations plus the honest third answer, because "we could not tell"
///     and "it is node-local" must never be the same value on a gate that decides whether node-local data leaves the
///     machine.
/// </remarks>
public enum ModelTrustLocality
{
    /// <summary>
    ///     Positively resolved as staying inside the trust boundary — a node-local runtime, or an external connection
    ///     the operator declared Local. The only value that earns local privileges.
    /// </summary>
    Local = 0,

    /// <summary>Positively resolved as leaving the trust boundary: Codex, Azure Foundry, or a declared-Cloud external connection.</summary>
    Cloud = 1,

    /// <summary>
    ///     An id that could not be resolved: a malformed <c>ext:</c> id, a connection deleted mid-turn, a store that
    ///     will not decrypt, or a lookup (external registry or cloud routing snapshot) that threw.
    /// </summary>
    /// <remarks>
    ///     Treated EXACTLY as <see cref="Cloud" /> by every gate and as not-routable by the send path — the fail-closed
    ///     posture. Kept distinct from
    ///     <see cref="Cloud" /> only so a caller can log or message it honestly.
    /// </remarks>
    Unresolved = 2
}

/// <summary>
///     The single place that answers "does sending to this model leave the node?", for external ids and node-local
///     ones alike.
/// </summary>
/// <remarks>
///     The TRUST authority: policy gates ask it, never <c>IsCodexModel</c> or <c>IsCloudProviderSelected</c>, which see no
///     <c>ext:</c> id and need a live Codex session. A Codex id is cloud; another non-external id is cloud when the routing
///     snapshot selects a provider; an external id is cloud unless declared local; a lookup failure is Unresolved. ROUTING
///     questions (which provider gets this send) stay on the cloud factory.
/// </remarks>
public interface IModelTrustResolver
{
    /// <summary>
    ///     Classifies <paramref name="modelId" />, resolving an <c>ext:</c> id through the registry. Fails closed to
    ///     <see cref="ModelTrustLocality.Unresolved" />; a blank id is <see cref="ModelTrustLocality.Local" />.
    /// </summary>
    Task<ModelTrustLocality> ResolveAsync(string? modelId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves the registration behind an <c>ext:</c> id, or <see langword="null" /> for a non-external or
    ///     unresolvable id. For call sites that need the declarations themselves (capabilities, context length, the
    ///     owning connection) and not just the locality.
    /// </summary>
    Task<ExternalProviderModelRegistration?> TryResolveExternalAsync(string? modelId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same rule as <see cref="ResolveAsync" />, synchronously, for the gates that have no async boundary (the
    ///     tool offer): an <c>ext:</c> id answers from the registry's cached generation
    ///     (<see cref="ClassifyExternalCached" />), so a cold cache is <see cref="ModelTrustLocality.Unresolved" />.
    /// </summary>
    ModelTrustLocality Classify(string? modelId);

    /// <summary>
    ///     The synchronous external-only classification for the dev-mode egress backstop.
    /// </summary>
    /// <remarks>
    ///     Answers ONLY about external ids, from the registry's cached generation. A non-external id returns
    ///     <see langword="null" />, meaning "not my question — the caller's existing cloud flag decides"; every other
    ///     answer, including a cold cache, resolves to a concrete <see cref="ModelTrustLocality" /> so the caller never
    ///     has to distinguish "no answer" from "safe".
    /// </remarks>
    ModelTrustLocality? ClassifyExternalCached(string? modelId);
}
