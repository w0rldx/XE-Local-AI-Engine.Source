namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;

/// <summary>
///     Last-resort, dependency-free crash breadcrumb for the managed host's earliest startup — the region that runs
///     BEFORE <c>Log.Logger</c> is assigned a real sink. Never throws.
/// </summary>
/// <remarks>
///     That region is the Velopack bootstrap and the desktop data-dir / operator-key bootstrap. An exception there is
///     caught by the top-level handler, but <c>Serilog.Log</c> is still the silent default logger, so nothing reaches
///     disk and the process dies with an empty logs folder. This writes straight to the SAME per-user logs directory
///     the rolling Serilog file uses (<c>%LOCALAPPDATA%\XE-Local-AI-Engine\logs</c> on Windows,
///     <c>$XDG_DATA_HOME|~/.local/share/...</c> on *nix), where a bug report already looks.
/// </remarks>
internal static class StartupCrashLog
{
    private const string LogFileName = "startup-crash.log";

    internal static Task RecordAsync(string message, CancellationToken cancellationToken = default)
    {
        var directory = ResolveLogDirectory();
        return directory is null ? Task.CompletedTask : RecordToAsync(directory, message, cancellationToken);
    }

    internal static string? ResolveLogDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        if ((configured is not null && string.IsNullOrWhiteSpace(configured))
            || !DesktopBootstrap.TryResolveDataDirectoryPath(out var root, out _))
        {
            return null;
        }

        return Path.Combine(root, "logs");
    }

    /// <summary>Directory-injected core (mirrors DesktopBootstrap's resolver seam) so the write path is testable without
    ///     touching the real per-user profile.</summary>
    internal static async Task RecordToAsync(string directory, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"[{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}");
            await File.AppendAllTextAsync(Path.Combine(directory, LogFileName), line, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            // Best-effort: a diagnostics write must never mask or replace the startup failure it is recording.
        }
    }

    internal static Task RecordAsync(string context, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return RecordAsync($"{context}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}",
            cancellationToken);
    }
}
