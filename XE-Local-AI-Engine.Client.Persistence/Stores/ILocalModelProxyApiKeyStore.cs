namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the SINGLE inbound model-proxy bearer credential. The store never sees the plaintext
///     key — it persists only its SHA-256 digest, which the node encryption interceptors additionally seal at rest. This
///     store owns only id/timestamp stamping and the singleton upsert rule; key generation, hashing and comparison are
///     the application layer's responsibility.
///     <para>
///         Guards the inbound OpenAI-compatible model proxy. Unrelated to <see cref="IMcpServerApiKeyStore" />, which
///         guards the inbound MCP tool surface.
///     </para>
/// </summary>
public interface ILocalModelProxyApiKeyStore
{
    /// <summary>
    ///     Returns the current credential — prefix, key digest and timestamps — or <see langword="null" /> when no key
    ///     has been generated yet (the default state of a fresh node — the proxy then authenticates nobody).
    /// </summary>
    Task<LocalModelProxyApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Upserts the singleton row, REPLACING any existing key, and returns the stored record. Replacing rather than
    ///     appending is what makes an old key stop working the moment a new one is generated; there is deliberately no
    ///     window in which both authenticate.
    /// </summary>
    Task<LocalModelProxyApiKeyRecord> SetAsync(string prefix, ReadOnlyMemory<byte> keyHash, CancellationToken cancellationToken = default);

    /// <summary>Removes the credential. Returns <see langword="true" /> when a row was deleted.</summary>
    Task<bool> DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a successful authentication timestamp. Deliberately does NOT touch the hash column, so a last-used
    ///     stamp never rewrites (or re-seals) the credential. A no-op when no key exists.
    /// </summary>
    Task TouchLastUsedAsync(long timestampUtc, CancellationToken cancellationToken = default);
}

/// <summary>
///     The stored inbound model-proxy credential. <see cref="KeyHash" /> is a one-way SHA-256 digest, not the key:
///     nothing on this record can be presented to the proxy endpoint, so no field here needs "reveal the key" handling.
///     The plaintext key exists only in the return value of the generate call that minted it.
/// </summary>
public sealed record LocalModelProxyApiKeyRecord(string Prefix, ReadOnlyMemory<byte> KeyHash, long CreatedAtUtc, long? LastUsedAtUtc);
