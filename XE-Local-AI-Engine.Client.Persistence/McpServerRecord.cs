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
public sealed record McpServerRecord
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required McpTransportKind TransportKind { get; init; }

    public required string? Command { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string? WorkingDirectory { get; init; }

    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public required string? Url { get; init; }

    public required McpTrustTier TrustTier { get; init; }

    public required bool Enabled { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}
