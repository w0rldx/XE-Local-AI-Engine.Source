namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Where a model's prompts actually go, as the policy gates need to know it. Deliberately TRI-state: the two
///     positive declarations plus the honest third answer, because "we could not tell" and "it is node-local" must
///     never be the same value on a gate that decides whether node-local data leaves the machine.
/// </summary>
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
    ///     An <c>ext:</c> id that could not be resolved: a malformed id, a connection deleted mid-turn, a store that
    ///     will not decrypt, or a lookup that threw. Treated EXACTLY as <see cref="Cloud" /> by every gate, and as
    ///     not-routable by the send path — the fail-closed posture the cloud routing classifier already sets the
    ///     precedent for. Kept distinct from <see cref="Cloud" /> only so a caller can log or message it honestly.
    /// </summary>
    Unresolved = 2
}

/// <summary>
///     The single place that answers "does sending to this model leave the node?", for external ids and node-local
///     ones alike.
/// </summary>
/// <remarks>
///     <para>
///         It exists because the answer used to be spelled three different ways in eight places
///         (<c>IsCloudProviderSelected</c>, <c>IsCodexModel</c>, <c>ResolveActiveCloudProviderName</c>), and none of
///         those three could see an <c>ext:</c> id at all — an unrecognized id falls through the cloud selection by
///         design, so a declared-cloud external model would have been classified node-local by every one of them.
///     </para>
///     <para>
///         The policy formula every gate applies is: an id is treated as cloud when the existing cloud checks say so
///         OR its external trust is anything other than <see cref="ModelTrustLocality.Local" />.
///     </para>
/// </remarks>
public interface IModelTrustResolver
{
    /// <summary>
    ///     Classifies <paramref name="modelId" />. A non-external id delegates to the existing cloud checks and can
    ///     never come back <see cref="ModelTrustLocality.Unresolved" />; an <c>ext:</c> id is resolved through the
    ///     registry and fails closed.
    /// </summary>
    Task<ModelTrustLocality> ResolveAsync(string? modelId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves the registration behind an <c>ext:</c> id, or <see langword="null" /> for a non-external or
    ///     unresolvable id. For call sites that need the declarations themselves (capabilities, context length, the
    ///     owning connection) and not just the locality.
    /// </summary>
    Task<ExternalProviderModelRegistration?> TryResolveExternalAsync(string? modelId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The synchronous classification for the send-path gates that have no async boundary — the tool offer and the
    ///     dev-mode egress backstop.
    /// </summary>
    /// <remarks>
    ///     Answers ONLY about external ids, from the registry's cached generation. A non-external id returns
    ///     <see langword="null" />, meaning "not my question — the caller's existing cloud flag decides"; every other
    ///     answer, including a cold cache, resolves to a concrete <see cref="ModelTrustLocality" /> so the caller never
    ///     has to distinguish "no answer" from "safe".
    /// </remarks>
    ModelTrustLocality? ClassifyExternalCached(string? modelId);
}
