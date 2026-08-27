namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The write-side and synchronous-read side of the external-provider registry, kept OUT of
///     <see cref="IExternalProviderRegistry" /> because that contract lives in the seam layer and describes only what a
///     provider needs: three async reads.
/// </summary>
/// <remarks>
///     Two application-layer concerns need more than that. The save/delete path must be able to drop the cached
///     generation the moment the encrypted file changes, and three policy sites on the synchronous send path have no
///     async boundary to await a refresh on.
/// </remarks>
public interface IExternalProviderRegistryCache
{
    /// <summary>
    ///     Drops the cached generation so the next read re-projects the encrypted store. Called by the save/delete path
    ///     AFTER the file has committed, never before: invalidating first would let a concurrent read re-cache the OLD
    ///     file and then never see the new one.
    /// </summary>
    void Invalidate();

    /// <summary>
    ///     Loads the snapshot if it is not cached, so the synchronous classification below has something to answer from
    ///     before the first chat turn. Called by the startup reconciliation pass.
    /// </summary>
    Task PrimeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves <paramref name="modelId" /> from the CACHED generation only, never touching disk.
    /// </summary>
    /// <param name="modelId">The candidate namespaced model id.</param>
    /// <param name="registration">
    ///     The registration on a hit, or <see langword="null" /> when the id is malformed or is not registered — which,
    ///     when this method returns <see langword="true" />, is a definitive "no such external model".
    /// </param>
    /// <returns>
    ///     <see langword="false" /> when nothing is cached yet, which is NOT "not registered": the caller must treat it
    ///     as unresolved and fail closed rather than as a benign miss.
    /// </returns>
    bool TryClassifyCached(string modelId, out ExternalProviderModelRegistration? registration);
}
