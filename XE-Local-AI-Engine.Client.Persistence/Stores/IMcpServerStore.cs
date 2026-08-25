namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for MCP server registrations. <c>Description</c>, <c>ArgumentsJson</c> and
///     <c>EnvJson</c> are encrypted at rest by the node encryption interceptors; reads return them decrypted (and
///     materialized into typed collections) on the <see cref="McpServerRecord" />. This store performs no content
///     validation — that is the application-layer service's responsibility; it owns only id/version/timestamp stamping
///     and the connection-affecting version-bump rule.
/// </summary>
public interface IMcpServerStore
{
    /// <summary>
    ///     Persists a new registration (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and
    ///     <c>Version = 1</c>, and forcing <c>Enabled = false</c> regardless of the input) and returns the stored record
    ///     with secret columns decrypted.
    /// </summary>
    Task<McpServerRecord> AddAsync(McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the registration identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and incrementing <c>Version</c> only when a connection-affecting field changed (transport,
    ///     command, arguments, environment, url, or the enabled toggle — never Name/Description alone). Returns the
    ///     updated record, or <c>null</c> when no registration has that id.
    /// </summary>
    Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Flips only the <c>Enabled</c> flag of the registration identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and bumping <c>Version</c> once when the flag actually changes. Unlike
    ///     <see cref="UpdateAsync" /> this does not touch (or re-encrypt) the args/env/description columns, so toggling
    ///     enablement neither rewrites secret ciphertext nor double-bumps <c>Version</c> across an enable/disable cycle.
    ///     Returns the updated record, or <c>null</c> when no registration has that id.
    /// </summary>
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
///     Mutable fields of an MCP server registration supplied on create/update. Free text is passed as plaintext
///     strings/collections; the store encodes <see cref="Description" /> and the arguments/environment to UTF-8 JSON
///     bytes before the interceptors encrypt them. On create, <see cref="Enabled" /> is ignored and the registration is
///     persisted disabled.
/// </summary>
public sealed record McpServerInput(
    string Name,
    string? Description,
    McpTransportKind TransportKind,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? Url,
    McpTrustTier TrustTier,
    bool Enabled);
