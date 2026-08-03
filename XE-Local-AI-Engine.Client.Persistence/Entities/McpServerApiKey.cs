namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The single node-level bearer credential that authenticates an EXTERNAL MCP client (Claude Code, Claude Desktop,
///     an IDE) against this node's inbound MCP server endpoint. Exactly one row exists at a time: generating a key
///     replaces the previous one, which is what makes the credential "replaceable" without introducing a key lifecycle
///     the single-user product does not need.
///     <para>
///         <b>Not to be confused with <see cref="McpServerRegistration" />.</b> That entity describes OUTBOUND
///         connections this node makes to third-party MCP servers. This one guards the INBOUND direction. The two are
///         independent and share nothing but the protocol name.
///     </para>
/// </summary>
internal sealed record class McpServerApiKey
{
    /// <summary>
    ///     The fixed primary key of the singleton row. The row is keyed by a constant rather than a fresh
    ///     <see cref="Guid" /> so "replace the key" is an upsert against a known id — there is no window in which two
    ///     rows exist and no ordering rule needed to decide which one authenticates.
    /// </summary>
    public static readonly Guid SingletonId = new("6b1f0f2a-6f2f-4c1f-9d3e-7a4c0b5e8d21");

    public Guid Id { get; set; }

    /// <summary>
    ///     The key's non-secret display prefix (the scheme marker plus the first few characters of the secret), used to
    ///     identify WHICH key a node holds without revealing it — safe for logs, audit records and the settings list.
    ///     Plaintext; structural.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>
    ///     The full key as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>mcp_api_key_material</c>.
    ///     <para>
    ///         Stored reversibly rather than as a one-way hash by deliberate product decision: this is a single-user,
    ///         loopback-only node, and the operator must be able to re-read the key to paste it into a client config
    ///         without invalidating every already-configured client. The threat this defends against is an attacker who
    ///         reads the database file, which the node encryption key already covers.
    ///     </para>
    /// </summary>
    public byte[] Material { get; set; } = [];

    public long CreatedAtUtc { get; set; }

    /// <summary>
    ///     Last successful authentication, or <see langword="null" /> if the key has never been used. Coarse operational
    ///     signal for the settings UI ("this key has never been used" is how an operator notices a misconfigured client);
    ///     deliberately not a full audit trail.
    /// </summary>
    public long? LastUsedAtUtc { get; set; }
}
