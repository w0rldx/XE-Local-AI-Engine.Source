namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over <see cref="IMcpServerStore" />: it validates the supplied fields,
///     delegates persistence and republishes the live MCP tool snapshot after any change to the enabled set.
/// </summary>
/// <remarks>
///     The store owns id/version/timestamp stamping and the connection-affecting version-bump rule. Validation rejects
///     an empty Name, missing transport-specific fields (Stdio requires a Command, Http a loopback Url), a non-loopback
///     HTTP URL and a duplicate Name, with the unique index as the backstop. A registration always persists disabled:
///     enabling is the dedicated <see cref="SetEnabledAsync" /> action.
/// </remarks>
public interface IMcpServerService
{
    /// <summary>Validates and persists a new registration (always disabled), returning the stored record.</summary>
    Task<McpServerRecord> CreateAsync(McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies the editable fields of <paramref name="input" /> to the registration with
    ///     <paramref name="id" />, preserving its current enabled state (enabling is <see cref="SetEnabledAsync" />).
    ///     Returns the updated record, or <c>null</c> when no registration has that id.
    /// </summary>
    Task<McpServerRecord?> UpdateAsync(Guid id, McpServerInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enables or disables the registration with <paramref name="id" /> without touching its other fields. Returns the
    ///     updated record, or <c>null</c> when no registration has that id.
    /// </summary>
    Task<McpServerRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Removes the registration with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no registration has that id.</summary>
    Task<McpServerRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every registered server, oldest first.</summary>
    Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The tool-panel view of the registration with <paramref name="id" />: its status verdict plus discovered tools.
    ///     Returns <c>null</c> when no registration has that id.
    /// </summary>
    Task<McpServerToolsView?> GetToolsViewAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>The tool-panel status of one registered MCP server.</summary>
public enum McpServerToolsStatus
{
    /// <summary>The server is not enabled, so no connection is attempted.</summary>
    Disabled = 0,

    /// <summary>Enabled with no recorded failure yet: a refresh has not reached it, or is still in flight.</summary>
    Connecting = 1,

    /// <summary>The last refresh connected it and listed its tools.</summary>
    Connected = 2,

    /// <summary>An actually recorded failure: a status entry exists, it is not connected, and a redacted reason was captured.</summary>
    Error = 3
}

/// <summary>The status verdict, recorded error and discovered tools for one registered MCP server.</summary>
public sealed class McpServerToolsView
{
    public required McpServerToolsStatus Status { get; init; }

    /// <summary>The redacted reason; set only for <see cref="McpServerToolsStatus.Error" />.</summary>
    public required string? Error { get; init; }

    /// <summary>Discovered tools; empty unless <see cref="McpServerToolsStatus.Connected" />.</summary>
    public required IReadOnlyList<McpServerToolInfo> Tools { get; init; }
}

/// <summary>Thrown when an MCP server create/update fails validation. The message is safe to surface to callers.</summary>
public sealed class McpServerValidationException : Exception
{
    public McpServerValidationException(string message) : base(message)
    {
    }

    public McpServerValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
