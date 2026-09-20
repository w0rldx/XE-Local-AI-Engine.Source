namespace XE_Local_AI_Engine.Client.Services.Mcp;

using XE_Local_AI_Engine.Client.Persistence;
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
    ///     The live per-server connection state from the connection manager (connected flag, discovered tool count, and a
    ///     redacted last error). Servers not yet seen by a refresh are absent.
    /// </summary>
    IReadOnlyList<McpServerConnectionStatus> GetConnectionStatuses();
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
