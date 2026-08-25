namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class McpServerRegistration
{
    public Guid Id { get; set; }

    /// <summary>Display label and slug source for qualified tool names. Plaintext; unique index. Not encrypted.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Optional free-text description as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>description</c>.
    /// </summary>
    public byte[]? Description { get; set; }

    /// <summary>Backing int for <see cref="McpTransportKind" />. Plaintext (structural).</summary>
    public int TransportKind { get; set; }

    /// <summary>Stdio transport: executable path. Plaintext (structural).</summary>
    public string? Command { get; set; }

    /// <summary>
    ///     Stdio transport: JSON array of arguments as UTF-8 bytes (args may carry tokens). Plaintext while tracked in
    ///     memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>arguments</c>.
    /// </summary>
    public byte[]? ArgumentsJson { get; set; }

    /// <summary>Stdio transport: working directory. Plaintext (structural).</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    ///     Stdio transport: JSON map of environment variables as UTF-8 bytes (env vars / secrets). Plaintext while
    ///     tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>env</c>.
    /// </summary>
    public byte[]? EnvJson { get; set; }

    /// <summary>Http transport: loopback-only URL. Plaintext (structural; loopback-validated by the application layer).</summary>
    public string? Url { get; set; }

    /// <summary>
    ///     Backing int for <see cref="McpTrustTier" />. Plaintext (structural): the sandbox backend selector reads it
    ///     before any key is available, and a tier is not a secret. Existing rows migrate to
    ///     <see cref="McpTrustTier.Sandboxed" /> — see <c>docs/security/mcp-trust-tiers.md</c> for why that is the
    ///     column default rather than the tier that would have preserved the old behaviour silently.
    /// </summary>
    public int TrustTier { get; set; }

    /// <summary>False on register; the user must explicitly enable a server before the connection manager connects it.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Bumped on each connection-affecting update; feeds the runtime package version and config hash so a
    ///     registration edit invalidates resume the same way a server-side version bump does.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
