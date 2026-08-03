namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the SINGLE inbound-MCP bearer credential. The key material is encrypted at rest by
///     the node encryption interceptors; <see cref="GetAsync" /> returns it decrypted. This store owns only
///     id/timestamp stamping and the singleton upsert rule — key generation, formatting and comparison are the
///     application layer's responsibility.
///     <para>
///         Guards the INBOUND MCP endpoint. Unrelated to <see cref="IMcpServerStore" />, which describes OUTBOUND
///         connections to third-party MCP servers.
///     </para>
/// </summary>
public interface IMcpServerApiKeyStore
{
    /// <summary>
    ///     Returns the current credential with its material decrypted, or <see langword="null" /> when no key has been
    ///     generated yet (the default state of a fresh node — the MCP endpoint then authenticates nobody).
    /// </summary>
    Task<McpServerApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Upserts the singleton row, REPLACING any existing key, and returns the stored record. Replacing rather than
    ///     appending is what makes an old key stop working the moment a new one is generated; there is deliberately no
    ///     window in which both authenticate.
    /// </summary>
    Task<McpServerApiKeyRecord> SetAsync(string prefix, string material, CancellationToken cancellationToken = default);

    /// <summary>Removes the credential. Returns <see langword="true" /> when a row was deleted.</summary>
    Task<bool> DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a successful authentication timestamp. Deliberately does NOT touch the material column, so a
    ///     last-used stamp never rewrites (or re-encrypts) the secret. A no-op when no key exists.
    /// </summary>
    Task TouchLastUsedAsync(long timestampUtc, CancellationToken cancellationToken = default);
}

/// <summary>
///     The stored inbound-MCP credential. <see cref="Material" /> is the full secret in plaintext — never log it, never
///     put it in a DTO that is not explicitly the "reveal the key" response.
/// </summary>
public sealed record McpServerApiKeyRecord(string Prefix, string Material, long CreatedAtUtc, long? LastUsedAtUtc);
