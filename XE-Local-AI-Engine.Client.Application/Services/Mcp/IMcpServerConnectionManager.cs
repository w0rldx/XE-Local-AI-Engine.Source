namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Owns the lifecycle of the node's MCP client connections. On <see cref="RefreshAsync" /> it reconciles the set of
///     live <c>McpClient</c>s against the enabled registrations (connecting new/changed servers, disposing
///     removed/disabled/version-changed ones), discovers each server's tools, renames them to collision-free qualified
///     names, wraps them for approval, and pushes the resulting immutable snapshot into the MCP tool registry that the
///     invocation factory and loopback offer provider read. A failed server is isolated: it contributes zero tools and
///     never aborts the others or the refresh. The CRUD service calls <see cref="RefreshAsync" /> after any change that
///     alters the enabled set; <see cref="GetStatuses" /> exposes per-server connection state to the management UI.
/// </summary>
public interface IMcpServerConnectionManager
{
    /// <summary>
    ///     Reconciles live connections against the enabled registrations and republishes the MCP tool snapshot. Serialized
    ///     so concurrent callers (startup connector + a CRUD mutation) cannot interleave; a per-server connect/list
    ///     timeout and per-server failure isolation keep one bad server from stalling or aborting the refresh.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     A point-in-time snapshot of each registered server's connection state for the management UI. <c>LastError</c>
    ///     is redacted (no host paths or secrets). Reflects the last <see cref="RefreshAsync" />; servers not seen by a
    ///     refresh yet are absent.
    /// </summary>
    IReadOnlyList<McpServerConnectionStatus> GetStatuses();
}

/// <summary>
///     Per-server connection state surfaced to the management UI. <see cref="LastError" /> carries a short, redacted
///     reason when the last connect/list attempt failed (no host paths or secrets); it is <c>null</c> when the server is
///     connected. <see cref="Tools" /> lists the server's discovered tools (the qualified names + descriptions +
///     approval flags the management panel renders); it is empty for a disabled or errored server.
/// </summary>
public sealed record McpServerConnectionStatus
{
    public required Guid ServerId { get; init; }

    public required string Name { get; init; }

    public required bool Connected { get; init; }

    public required int ToolCount { get; init; }

    public string? LastError { get; init; }

    public required IReadOnlyList<McpServerToolInfo> Tools { get; init; }
}

/// <summary>
///     One discovered tool on a connected MCP server, for the management panel. <see cref="Name" /> is the qualified
///     tool name (<c>mcp__{serverSlug}__{tool}</c>) — the authoritative offered/executable name; a client may strip the
///     prefix for display. Every MCP tool ships <see cref="RequiresApproval" /> = <c>true</c> by default.
/// </summary>
public sealed record McpServerToolInfo
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool RequiresApproval { get; init; }
}
