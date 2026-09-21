namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The single node-level bearer credential that authenticates an EXTERNAL tool (a local agent, an OpenAI-compatible
///     client such as LiteLLM/Continue, a Hermes-style agent) against this node's inbound OpenAI-compatible model PROXY.
/// </summary>
/// <remarks>
///     Exactly one row exists at a time: generating a key replaces the previous one, which is what makes the
///     credential "replaceable" without a key lifecycle the single-user product does not need. Deliberately SEPARATE
///     from <see cref="McpServerApiKey" />, which guards the inbound MCP tool surface (this node's <em>agent</em>
///     tools); this one guards the raw-model proxy that serves only the LLM, with none of the persona/tools/memory/RAG
///     scaffolding. Either can be enabled, revoked or rotated alone: a raw-model tool never needs the MCP key.
/// </remarks>
internal sealed record class LocalModelProxyApiKey
{
    /// <summary>
    ///     The fixed primary key of the singleton row.
    /// </summary>
    /// <remarks>
    ///     Keyed by a constant rather than a fresh <see cref="Guid" /> so "replace the key" is an upsert against a
    ///     known id — there is no window in which two rows exist and no ordering rule needed to decide which one
    ///     authenticates.
    /// </remarks>
    public static readonly Guid SingletonId = new("2c9d5b74-0e1a-4f8b-9a3c-4d6e7f8a1b20");

    public Guid Id { get; set; }

    /// <summary>
    ///     The key's non-secret display prefix (the scheme marker plus the first few characters of the secret), used to
    ///     identify WHICH key a node holds without revealing it — safe for logs, audit records and the settings list.
    ///     Plaintext; structural.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>
    ///     The SHA-256 digest of the full key's UTF-8 bytes — 32 bytes, one way. The plaintext key exists only in the
    ///     response to the generate call that minted it, so a read of this row yields nothing presentable to the proxy.
    /// </summary>
    /// <remarks>
    ///     A plain digest rather than a password KDF is correct: the input is a 256-bit cryptographically random token,
    ///     so PBKDF2/Argon2 has no low-entropy guess space to slow down and would tax every authenticated proxy request.
    ///     No salt either: salt defends against precomputation across many low-entropy secrets, and there is exactly one here. Still encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and <see cref="NodeEncryptionMaterializationInterceptor" /> under AAD column name <c>local_model_proxy_api_key_hash</c>,
    ///     for INTEGRITY: a bare hash column would let a database WRITER substitute a digest whose preimage they know and take over the model-proxy surface.
    /// </remarks>
    public byte[] KeyHash { get; set; } = [];

    public long CreatedAtUtc { get; set; }

    /// <summary>
    ///     Last successful authentication, or <see langword="null" /> if the key has never been used. Coarse operational
    ///     signal for the settings UI ("this key has never been used" is how an operator notices a misconfigured client);
    ///     deliberately not a full audit trail.
    /// </summary>
    public long? LastUsedAtUtc { get; set; }
}
