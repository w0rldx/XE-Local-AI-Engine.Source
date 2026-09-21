namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The single node-level bearer credential that authenticates an EXTERNAL MCP client (Claude Code, Claude Desktop,
///     an IDE) against this node's inbound MCP server endpoint.
/// </summary>
/// <remarks>
///     Exactly one row exists at a time: generating a key replaces the previous one, which is what makes the
///     credential "replaceable" without introducing a key lifecycle the single-user product does not need.
///     <b>Not to be confused with <see cref="McpServerRegistration" />.</b> That entity describes OUTBOUND connections
///     this node makes to third-party MCP servers; this one guards the INBOUND direction. The two are independent and
///     share nothing but the protocol name.
/// </remarks>
internal sealed record class McpServerApiKey
{
    /// <summary>
    ///     The fixed primary key of the singleton row.
    /// </summary>
    /// <remarks>
    ///     Keyed by a constant rather than a fresh <see cref="Guid" /> so "replace the key" is an upsert against a
    ///     known id — there is no window in which two rows exist and no ordering rule needed to decide which one
    ///     authenticates.
    /// </remarks>
    public static readonly Guid SingletonId = new("6b1f0f2a-6f2f-4c1f-9d3e-7a4c0b5e8d21");

    public Guid Id { get; set; }

    /// <summary>
    ///     The key's non-secret display prefix (the scheme marker plus the first few characters of the secret), used to
    ///     identify WHICH key a node holds without revealing it — safe for logs, audit records and the settings list.
    ///     Plaintext; structural.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>
    ///     The SHA-256 digest of the full key's UTF-8 bytes — 32 bytes, one way. The plaintext key exists only in the
    ///     response to the generate call that minted it, so a read of this row yields nothing presentable to the MCP
    ///     endpoint.
    /// </summary>
    /// <remarks>
    ///     A plain digest rather than a password KDF is correct: the input is a 256-bit cryptographically random token,
    ///     so PBKDF2/Argon2 has no low-entropy guess space to slow down and would tax every authenticated MCP request.
    ///     No salt either: salt defends against precomputation across many low-entropy secrets, and there is exactly one here. Still encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and <see cref="NodeEncryptionMaterializationInterceptor" /> under AAD column name <c>mcp_api_key_hash</c>,
    ///     for INTEGRITY: a bare hash column would let a database WRITER substitute a digest whose preimage they know and take over an agent-execution surface.
    /// </remarks>
    public byte[] KeyHash { get; set; } = [];

    /// <summary>The authority granted to callers presenting this credential. Existing rows backfill to delegate.</summary>
    public int Scope { get; set; }

    /// <summary>Changes on every rotation so authentication-side effects can target only the key that was validated.</summary>
    public Guid GenerationId { get; set; }

    public long CreatedAtUtc { get; set; }

    /// <summary>
    ///     Last successful authentication, or <see langword="null" /> if the key has never been used. Coarse operational
    ///     signal for the settings UI ("this key has never been used" is how an operator notices a misconfigured client);
    ///     deliberately not a full audit trail.
    /// </summary>
    public long? LastUsedAtUtc { get; set; }
}
