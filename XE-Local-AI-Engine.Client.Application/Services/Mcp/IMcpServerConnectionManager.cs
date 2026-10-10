namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Owns the lifecycle of the node's MCP client connections.
/// </summary>
/// <remarks>
///     A refresh reconciles live <c>McpClient</c>s against the enabled registrations, discovers and qualifies each
///     server's tools and pushes the snapshot into the MCP tool registry. A failed server contributes no tools; one that
///     dies after connecting keeps its tools offered, and the next call reconnects once or fails typed. The CRUD service
///     refreshes the one changed registration; <see cref="GetStatuses" /> feeds the management UI.
/// </remarks>
public interface IMcpServerConnectionManager
{
    /// <summary>
    ///     Reconciles live connections against the enabled registrations and republishes the MCP tool snapshot.
    /// </summary>
    /// <remarks>
    ///     It is serialized, so the startup connector and a CRUD mutation cannot interleave, and a per-server connect
    ///     and list timeout plus per-server failure isolation keep one bad server from stalling or aborting a refresh.
    /// </remarks>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reconciles one registration, (re)connecting it when enabled and not live or changed, or withdrawing its tools
    ///     when disabled or deleted. Other servers are untouched.
    /// </summary>
    Task RefreshAsync(Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The operator's Reconnect: closes every session of one server and reconnects it even when it looks live, since a
    ///     connected server can still be stuck. A disabled or deleted server is withdrawn, as by a refresh.
    /// </summary>
    Task ReconnectAsync(Guid serverId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Withdraws one server at once, without waiting for a refresh: its sessions are closed and a call to its tools fails
    ///     typed from then on. The CRUD service awaits it on disable and delete; a later refresh republishes the snapshot.
    /// </summary>
    Task RevokeAsync(Guid serverId);

    /// <summary>
    ///     A point-in-time snapshot of each server's connection state for the management UI. <c>LastError</c> is
    ///     redacted (no host paths or secrets); servers no refresh has seen yet are absent.
    /// </summary>
    IReadOnlyList<McpServerConnectionStatus> GetStatuses();
}

/// <summary>
///     Per-server connection state surfaced to the management UI.
/// </summary>
/// <remarks>
///     <see cref="LastError" /> carries a short, redacted reason when the last connect or list attempt failed, with no
///     host paths or secrets, and is <c>null</c> for a connected server; <see cref="FailureReason" /> classifies the same
///     failure so the panel can word it, and is <c>null</c> exactly when <see cref="LastError" /> is. <see cref="Tools" /> lists the discovered
///     tools the management panel renders — qualified names, descriptions, approval flags — and is empty for a
///     disabled or errored server.
/// </remarks>
public sealed record McpServerConnectionStatus
{
    public required Guid ServerId { get; init; }

    public required string Name { get; init; }

    public required bool Connected { get; init; }

    public required int ToolCount { get; init; }

    public string? LastError { get; init; }

    public McpConnectionFailureReason? FailureReason { get; init; }

    public required IReadOnlyList<McpServerToolInfo> Tools { get; init; }
}

/// <summary>
///     One discovered tool on a connected MCP server, for the management panel.
/// </summary>
/// <remarks>
///     <see cref="Name" /> is the qualified tool name, <c>mcp__{serverSlug}__{tool}</c>, which is the authoritative
///     offered and executable name; a client may strip the prefix for display. Every MCP tool ships requiring
///     approval by default.
/// </remarks>
public sealed record McpServerToolInfo
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool RequiresApproval { get; init; }
}
