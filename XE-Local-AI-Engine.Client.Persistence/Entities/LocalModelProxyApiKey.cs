namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The single node-level bearer credential that authenticates an EXTERNAL tool (a local agent, an OpenAI-compatible
///     client such as LiteLLM/Continue, a Hermes-style agent) against this node's inbound OpenAI-compatible model PROXY.
///     Exactly one row exists at a time: generating a key replaces the previous one, which is what makes the credential
///     "replaceable" without a key lifecycle the single-user product does not need.
///     <para>
///         Deliberately SEPARATE from <see cref="McpServerApiKey" />. That credential guards the inbound MCP tool
///         surface (which offers this node's <em>agent</em> tools); this one guards the raw-model proxy that serves only
///         the LLM, with none of the node's persona/tools/memory/RAG scaffolding. The two are independent so an operator
///         can enable, revoke, or rotate one without touching the other — an external tool that should only see the raw
///         model never needs, and never gets, the MCP key.
///     </para>
/// </summary>
internal sealed record class LocalModelProxyApiKey
{
    /// <summary>
    ///     The fixed primary key of the singleton row. Keyed by a constant rather than a fresh <see cref="Guid" /> so
    ///     "replace the key" is an upsert against a known id — there is no window in which two rows exist and no ordering
    ///     rule needed to decide which one authenticates.
    /// </summary>
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
    ///     response to the generate call that minted it and is never written anywhere; a read of this row therefore
    ///     yields nothing an attacker can present to the proxy endpoint.
    ///     <para>
    ///         A plain digest rather than a password KDF is correct here: the input is a 256-bit cryptographically
    ///         random token, so there is no low-entropy guess space for PBKDF2/Argon2 to slow down, and a KDF would tax
    ///         every authenticated proxy request. No salt for the same reason — salt defends against precomputation
    ///         across many low-entropy secrets, and there is exactly one high-entropy secret here.
    ///     </para>
    ///     <para>
    ///         Still encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///         <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///         <c>local_model_proxy_api_key_hash</c>, for INTEGRITY rather than confidentiality: hashing defends the read
    ///         direction, but a bare hash column would let anyone who can WRITE the database file substitute a digest
    ///         whose preimage they know and take over the model-proxy surface. The AAD-bound AEAD is what makes that
    ///         substitution fail.
    ///     </para>
    /// </summary>
    public byte[] KeyHash { get; set; } = [];

    public long CreatedAtUtc { get; set; }

    /// <summary>
    ///     Last successful authentication, or <see langword="null" /> if the key has never been used. Coarse operational
    ///     signal for the settings UI ("this key has never been used" is how an operator notices a misconfigured client);
    ///     deliberately not a full audit trail.
    /// </summary>
    public long? LastUsedAtUtc { get; set; }
}
