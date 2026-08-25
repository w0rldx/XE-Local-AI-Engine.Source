namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Decrypted, typed projection of a persisted <c>McpServerRegistration</c>. <see cref="Description" />,
///     <see cref="Arguments" /> and <see cref="Environment" /> are returned in plaintext (decrypted on
///     materialization); the JSON columns are materialized into typed collections. The store converts to and from this
///     shape at the boundary so callers never touch the encrypted byte columns or the raw JSON.
///     <para>
///         <see cref="TrustTier" /> is structural and plaintext: it decides where a stdio server's process runs, so it
///         has to be readable by the backend selector without a key. See <c>docs/security/mcp-trust-tiers.md</c>.
///     </para>
/// </summary>
public sealed record McpServerRecord(
    Guid Id,
    string Name,
    string? Description,
    McpTransportKind TransportKind,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? Url,
    McpTrustTier TrustTier,
    bool Enabled,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);
