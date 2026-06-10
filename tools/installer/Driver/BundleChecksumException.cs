namespace XE_Local_AI_Engine.Installer.Driver;

/// <summary>
///     Thrown when a payload file's SHA-256 does not match the bundle's <c>SHA256SUMS</c> (corruption
///     guard, plan §10). A dedicated type so the entry point can map it to
///     <c>InstallerExitCode.ChecksumMismatch</c> without inspecting the message text.
/// </summary>
public sealed class BundleChecksumException : Exception
{
    public BundleChecksumException(string message) : base(message)
    {
    }

    public BundleChecksumException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
