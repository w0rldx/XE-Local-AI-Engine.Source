namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One <c>xeint_</c> bearer credential for the external integration surface. Unlike the two singleton key rows in
///     this schema (<see cref="McpServerApiKey" />, <see cref="LocalModelProxyApiKey" />) a node holds <b>many</b> of
///     these, and several may belong to one integrator — which is what <see cref="PrincipalId" /> exists to say.
/// </summary>
internal sealed record class IntegrationApiKey
{
    public Guid Id { get; set; }

    /// <summary>
    ///     The stable integrator identity this credential belongs to (ruling R4-6). Ownership of every session and
    ///     execution, and request-id uniqueness, key on this and never on <see cref="KeyPrefix" />, so rotating a
    ///     credential or issuing a second one does not strand in-flight work. Plaintext (structural).
    /// </summary>
    public Guid PrincipalId { get; set; }

    /// <summary>Displayable prefix (<c>xeint_</c> plus eight characters); the auth lookup key. Plaintext (structural).</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    ///     One-way SHA-256 digest of the issued key as raw bytes. Plaintext while tracked in memory; encrypted at rest
    ///     by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>integration_api_key_hash</c>. Required — a row without it would authenticate nothing — so it always
    ///     encrypts. This is integrity, not confidentiality: a bare digest column lets anyone who can WRITE the
    ///     database file substitute a digest whose preimage they know and take over the surface.
    /// </summary>
    public byte[] KeyHash { get; set; } = [];

    /// <summary>Operator label for the credential. Plaintext (structural).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>JSON array of trigger ids this key may invoke; <c>null</c> means every trigger. Plaintext (structural).</summary>
    public string? AllowedTriggerIdsJson { get; set; }

    /// <summary>Unix-ms creation instant. Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Unix-ms instant the credential last authenticated, or null. Plaintext (structural).</summary>
    public long? LastUsedAtUtc { get; set; }

    /// <summary>
    ///     Unix-ms instant the credential was revoked, or null while live. Revocation is soft: execution and audit rows
    ///     reference <see cref="KeyPrefix" />, so the row is stamped and never deleted. Plaintext (structural).
    /// </summary>
    public long? RevokedAtUtc { get; set; }
}
