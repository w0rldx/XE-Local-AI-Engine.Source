namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for MCP server registrations.
/// </summary>
/// <remarks>
///     <c>Description</c>, <c>ArgumentsJson</c> and <c>EnvJson</c> are encrypted at rest by the node encryption
///     interceptors; reads return them decrypted (and materialized into typed collections) on the
///     <see cref="McpServerRecord" />. The store performs no content validation — that is the application-layer
///     service's responsibility; it owns only id/version/timestamp stamping and the connection-affecting bump rule.
/// </remarks>
public interface IMcpServerStore
{
    /// <summary>
    ///     Persists a new registration (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and
    ///     <c>Version = 1</c>, and forcing <c>Enabled = false</c> regardless of the input) and returns the stored record
    ///     with secret columns decrypted.
    /// </summary>
    Task<McpServerRecord> AddAsync(McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the registration identified by <paramref name="id" />, or returns
    ///     <c>null</c> when no registration has that id.
    /// </summary>
    /// <remarks>
    ///     Stamps <c>UpdatedAtUtc</c> and increments <c>Version</c> only when a connection-affecting field changed:
    ///     transport, command, arguments, environment, url, or the enabled toggle — never Name/Description alone.
    /// </remarks>
    Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Flips only the <c>Enabled</c> flag of the registration identified by <paramref name="id" />, or returns
    ///     <c>null</c> when no registration has that id.
    /// </summary>
    /// <remarks>
    ///     Stamps <c>UpdatedAtUtc</c> and bumps <c>Version</c> once when the flag actually changes. Unlike
    ///     <see cref="UpdateAsync" /> it does not touch (or re-encrypt) the args/env/description columns, so toggling
    ///     enablement neither rewrites secret ciphertext nor double-bumps <c>Version</c> across an enable/disable cycle.
    /// </remarks>
    Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Removes the registration with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no registration has that id.</summary>
    Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every registration, oldest first.</summary>
    Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns only the enabled registrations, oldest first — the set the connection manager connects.</summary>
    Task<IReadOnlyList<McpServerRecord>> ListEnabledAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of an MCP server registration supplied on create/update.
/// </summary>
/// <remarks>
///     Free text is passed as plaintext strings/collections; the store encodes <see cref="Description" /> and the
///     arguments/environment to UTF-8 JSON bytes before the interceptors encrypt them. On create,
///     <see cref="Enabled" /> is ignored and the registration is persisted disabled.
/// </remarks>
public sealed record McpServerInput
{
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
}
