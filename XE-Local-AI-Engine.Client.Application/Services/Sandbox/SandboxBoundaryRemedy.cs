namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     What an operator does when a workload that REQUIRES the filesystem boundary is refused, worded for the mechanism this host would
///     use: bubblewrap on Linux, the MXC AppContainer boundary (Preview) on Windows (ADR 0019).
/// </summary>
public static class SandboxBoundaryRemedy
{
    /// <summary>The remedy for the current host, as one clause that a refusal message continues with ", or …".</summary>
    public static string ForThisHost() =>
        ForHost(OperatingSystem.IsWindows());

    /// <summary>
    ///     The remedy SENTENCE for a filesystem-isolation refusal whose cause the provider named
    ///     (<see cref="ISandboxRuntimeProvider.FilesystemIsolationUnavailableReason" />): the previews switch when that is the cause,
    ///     otherwise <see cref="ForThisHost" />.
    /// </summary>
    public static string ForFilesystemIsolationCause(string? cause) =>
        IsExecutionPreviewsDisabled(cause)
            ? "An operator can turn on execution previews in the Sandbox & isolation section of Node settings."
            : ForThisHost() + ".";

    /// <summary>True when <paramref name="cause" /> says only the execution-previews switch is missing.</summary>
    public static bool IsExecutionPreviewsDisabled(string? cause) =>
        string.Equals(cause, Implementation.Launch.SandboxContainment.ExecutionPreviewsDisabledReason, StringComparison.Ordinal);

    // The platform is a parameter so both wordings are asserted on every host.
    internal static string ForHost(bool windowsHost) =>
        windowsHost
            ? "On Windows the boundary is the MXC AppContainer mechanism (Preview): it needs a Windows build on which MXC reports the ProcessContainer available, the one-time administrator wxc-host-prep preparation, and an operator enabling execution previews in Node settings; the sandbox isolation summary names which one is missing"
            : "Install bubblewrap (bwrap) together with the user-namespace support the sandbox containment probe reports as missing";
}
