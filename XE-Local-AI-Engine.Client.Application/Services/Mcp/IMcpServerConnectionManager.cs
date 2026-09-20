namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Owns the lifecycle of the node's MCP client connections.
/// </summary>
/// <remarks>
///     <see cref="RefreshAsync" /> reconciles the live <c>McpClient</c>s against the enabled registrations, discovers
///     each server's tools, renames them to collision-free qualified names, wraps them for approval and pushes the
///     immutable snapshot into the MCP tool registry the invocation factory and loopback offer provider read. A failed
///     server is isolated: it contributes zero tools and never aborts the others or the refresh. The CRUD service
///     refreshes after any change to the enabled set; <see cref="GetStatuses" /> feeds the management UI.
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
    ///     A point-in-time snapshot of each registered server's connection state for the management UI. <c>LastError</c>
    ///     is redacted (no host paths or secrets). Reflects the last <see cref="RefreshAsync" />; servers not seen by a
    ///     refresh yet are absent.
    /// </summary>
    IReadOnlyList<McpServerConnectionStatus> GetStatuses();
}

/// <summary>
///     Per-server connection state surfaced to the management UI.
/// </summary>
/// <remarks>
///     <see cref="LastError" /> carries a short, redacted reason when the last connect or list attempt failed, with no
///     host paths or secrets, and is <c>null</c> for a connected server. <see cref="Tools" /> lists the discovered
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
