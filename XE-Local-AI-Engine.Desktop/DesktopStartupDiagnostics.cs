namespace XE_Local_AI_Engine.Desktop;

using System.Globalization;

/// <summary>
///     Best-effort, dependency-free crash breadcrumb for the desktop shell. Never throws: a diagnostics failure must not
///     become a second failure on top of the one it is trying to record.
/// </summary>
/// <remarks>
///     A hand-kept copy of the WindowsLauncher's <c>StartupDiagnostics</c>: this project takes no project references.
///     The shell starts from a shortcut with no console, so anything written only to <see cref="Console.Error" /> is
///     lost ("window never appears, no logs"). This appends a timestamped line to the same per-user logs directory the
///     host's rolling Serilog file uses, where a bug report already looks.
/// </remarks>
internal static class DesktopStartupDiagnostics
{
    private const string LogFileName = "desktop.log";

    internal static void Record(string message)
    {
        var directory = ResolveLogDirectory(Environment.GetEnvironmentVariable("XE_DATA_DIR"));
        if (directory is not null)
        {
            RecordTo(directory, message);
        }
    }

    internal static string? ResolveLogDirectory(string? configured)
    {
        if (configured is null)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DesktopStartupOptions.ApplicationDataFolderName, "logs");
        }

        if (string.IsNullOrWhiteSpace(configured) || configured.Any(char.IsControl) || !Path.IsPathFullyQualified(configured))
        {
            return null;
        }

        try
        {
            return Path.Combine(Path.GetFullPath(configured), "logs");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Directory-injected core so the write path is testable without touching the real per-user profile.</summary>
    internal static void RecordTo(string directory, string message)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"[{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            // Forced sync: the callers are the synchronous STA Main catch and a UI-thread failure path, neither of
            // which can await (see Program.Main).
#pragma warning disable MA0045
            File.AppendAllText(Path.Combine(directory, LogFileName), line);
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Diagnostics are best-effort: if the per-user logs directory cannot be created or written (locked disk,
            // exotic profile), the failure being diagnosed still surfaces via Console.Error and the process exit code.
        }
    }
}
