namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     Thrown when an ingredient of the filesystem-isolated launch chain cannot be prepared on this host.
/// </summary>
/// <remarks>
///     An untrustworthy helper binary, a jail whose ancestors are writable by someone else, a descriptor that could not be opened without
///     traversing a symlink. It carries a MEASURED reason rather than a category, that reason being what the probe records and a host logs.
///     Nothing in the isolation layer degrades silently: every failure to prepare the boundary ends here and with
///     <c>SupportsFilesystemIsolation</c> false, never with a weaker chain that still runs.
/// </remarks>
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
