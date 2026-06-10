namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     Process exit codes. <see cref="Success" /> is 0; every failure (usage error, already-installed,
///     missing install, checksum mismatch, aborted preflight, partial teardown) is non-zero so a CI
///     harness or the manual runbook can branch on the result.
/// </summary>
public static class InstallerExitCode
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int AlreadyInstalled = 3;
    public const int NotInstalled = 4;
    public const int ChecksumMismatch = 5;
    public const int PreflightFailed = 6;
    public const int RebootRequired = 7;
    public const int TeardownIncomplete = 8;
    public const int Aborted = 9;
    public const int UnexpectedError = 10;
}
