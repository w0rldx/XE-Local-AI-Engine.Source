namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     How this process was launched, resolved once by the host from its arguments, environment and install before the
///     container is built, and registered as a singleton so no service re-reads the process arguments.
/// </summary>
/// <remarks>
///     A test host is never local mode: the host resolves the launch mode only on the real entry point, so the answer
///     does not depend on the test runner's own arguments.
/// </remarks>
public sealed class NodeLaunchContext
{
    /// <summary><see langword="true" /> for the desktop and MCP-only launches, which own a per-user local data directory.</summary>
    public required bool IsLocalMode { get; init; }
}
