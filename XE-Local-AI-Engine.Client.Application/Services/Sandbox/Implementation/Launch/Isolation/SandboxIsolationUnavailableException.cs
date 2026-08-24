namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Thrown when some ingredient of the filesystem-isolated launch chain cannot be prepared on this host: a helper
///     binary that is not trustworthy, a jail whose ancestors are writable by someone else, a descriptor that could
///     not be opened without traversing a symlink.
///     <para>
///         It carries a MEASURED reason rather than a category, because that reason is what the capability probe
///         records and what a degraded host logs. Nothing in the isolation layer degrades silently: every failure to
///         prepare the boundary ends here and ends with <c>SupportsFilesystemIsolation</c> false, never with a weaker
///         chain that still runs.
///     </para>
/// </summary>
public sealed class SandboxIsolationUnavailableException : Exception
{
    public SandboxIsolationUnavailableException(string message)
        : base(message)
    {
    }

    public SandboxIsolationUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
