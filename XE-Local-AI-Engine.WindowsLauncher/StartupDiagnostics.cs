namespace XE_Local_AI_Engine.WindowsLauncher;

using System.Globalization;

/// <summary>
///     Best-effort, dependency-free crash breadcrumb for the launcher. The launcher is a plain console process whose
///     window vanishes the instant it exits on a double-click, so every diagnostic it writes only to
///     <see cref="Console.Error" /> is lost before a user can read it — leaving a "flashes then closes, no logs"
///     report with nothing on disk to act on. This appends a timestamped line to the SAME per-user logs directory the
///     managed host's rolling Serilog file uses (<c>%LOCALAPPDATA%\XE-Local-AI-Engine\logs</c>), so a launcher-side
///     failure (missing runtime, incomplete payload, a non-zero managed exit) survives in the place a bug report
///     already looks. Never throws: a diagnostics failure must not become a second failure on top of the one it is
///     trying to record.
/// </summary>
internal static class StartupDiagnostics
{
    // Mirrors DesktopBootstrap.ApplicationDataFolderName in the managed Client (a separate assembly this launcher does
    // not reference); kept in sync by hand so both processes log under the same per-user root.
    private const string ApplicationDataFolderName = "XE-Local-AI-Engine";
    private const string LogFileName = "launcher.log";

    internal static void Record(string message) =>
        RecordTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationDataFolderName,
                "logs"),
            message);

    /// <summary>Directory-injected core (mirrors DesktopBootstrap's resolver seam) so the write path is testable without
    ///     touching the real per-user profile.</summary>
    internal static void RecordTo(string directory, string message)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"[{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            File.AppendAllText(Path.Combine(directory, LogFileName), line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Diagnostics are best-effort: if the per-user logs directory cannot be created or written (locked disk,
            // exotic profile), the failure being diagnosed still surfaces via Console.Error and the process exit code.
        }
    }
}
