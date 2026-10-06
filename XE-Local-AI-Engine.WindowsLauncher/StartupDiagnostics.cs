namespace XE_Local_AI_Engine.WindowsLauncher;

using System.Globalization;

/// <summary>
///     Best-effort, dependency-free crash breadcrumb for the launcher. Never throws: a diagnostics failure must not
///     become a second failure on top of the one it is trying to record.
/// </summary>
/// <remarks>
///     The launcher is a plain console process whose window vanishes the instant it exits on a double-click, so every
///     diagnostic written only to <see cref="Console.Error" /> is lost before a user can read it, leaving a "flashes
///     then closes, no logs" report with nothing on disk to act on. This appends a timestamped line to the SAME
///     per-user logs directory the managed host's rolling Serilog file uses
///     (<c>%LOCALAPPDATA%\XE-Local-AI-Engine\logs</c>), where a bug report already looks.
/// </remarks>
internal static class StartupDiagnostics
{
    // Mirrors DesktopBootstrap.ApplicationDataFolderName in the managed Client (a separate assembly this launcher does
    // not reference); kept in sync by hand so both processes log under the same per-user root.
    private const string ApplicationDataFolderName = "XE-Local-AI-Engine";
    private const string LogFileName = "launcher.log";

    // Append-only with no rotation: past MaxBytes, only the newest KeepBytes (from a line start) survive the next append.
    internal const long MaxBytes = 1024 * 1024;
    internal const int KeepBytes = 256 * 1024;

    internal static void Record(string message)
    {
        var directory = ResolveLogDirectory(Environment.GetEnvironmentVariable("XE_DATA_DIR"));
        if (directory is not null)
        {
            RecordTo(directory, message);
        }
    }

    /// <summary>
    ///     Hand copy of the Client's <c>ProcessCrashHooks</c> (this project takes no project references): an unhandled
    ///     exception and an unobserved task exception each append one line here; the task exception is marked observed.
    /// </summary>
    internal static void RegisterCrashHooks()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
            RecordCrash(ResolveLogDirectory(Environment.GetEnvironmentVariable("XE_DATA_DIR")), "Unhandled exception; the process is terminating", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            e.SetObserved();
            RecordCrash(ResolveLogDirectory(Environment.GetEnvironmentVariable("XE_DATA_DIR")), "Unobserved task exception", e.Exception);
        };
    }

    internal static void RecordCrash(string? directory, string context, object exceptionObject)
    {
        if (directory is not null)
        {
            RecordTo(directory, $"{context}: {exceptionObject}");
        }
    }

    internal static string? ResolveLogDirectory(string? configured)
    {
        if (configured is null)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationDataFolderName, "logs");
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

    /// <summary>Directory-injected core (mirrors DesktopBootstrap's resolver seam) so the write path is testable without
    ///     touching the real per-user profile.</summary>
    internal static void RecordTo(string directory, string message)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"[{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            // Forced sync: the only callers are the synchronous Fail/MissingRuntime result helpers and the synchronous
            // Velopack Main path, none of which can await (see Program.Main).
#pragma warning disable MA0045 // Contract-forced: sync Main caller.
            var path = Path.Combine(directory, LogFileName);
            TrimIfLarge(path);
            File.AppendAllText(path, line);
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Diagnostics are best-effort: if the per-user logs directory cannot be created or written (locked disk,
            // exotic profile), the failure being diagnosed still surfaces via Console.Error and the process exit code.
        }
    }

    /// <summary>
    ///     Hand copy of the Client's <c>StartupCrashLog.TrimIfLargeAsync</c> (this project takes no project
    ///     references): past <see cref="MaxBytes" /> the file keeps only its newest <see cref="KeepBytes" />, from a line
    ///     start. I/O errors reach <see cref="RecordTo" />'s catch.
    /// </summary>
    internal static void TrimIfLarge(string path)
    {
        if (new FileInfo(path) is not { Exists: true, Length: > MaxBytes })
        {
            return;
        }

        // Forced sync for the same reason as RecordTo's append: its only caller cannot await.
#pragma warning disable MA0045 // Contract-forced: sync Main caller.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var tail = new byte[KeepBytes];
        stream.Seek(-KeepBytes, SeekOrigin.End);
        stream.ReadExactly(tail);
        var start = Array.IndexOf(tail, (byte)'\n') + 1;
        stream.Position = 0;
        stream.Write(tail, start, tail.Length - start);
        stream.SetLength(tail.Length - start);
#pragma warning restore MA0045
    }
}
