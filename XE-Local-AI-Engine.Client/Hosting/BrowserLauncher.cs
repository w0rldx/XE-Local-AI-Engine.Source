namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics;

/// <summary>
///     Opens the default browser at the desktop loopback URL. The per-OS command is built by a pure function so it can be
///     unit-tested without launching a process, and the launch is non-fatal: if the browser cannot be started the server
///     keeps serving and the URL is logged so the user can open it manually.
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>
    ///     The default launch action used in production: starts the process with the configured
    ///     <see cref="ProcessStartInfo" />. Kept as a field so the <see cref="OpenBrowser" /> seam has a real default.
    /// </summary>
    internal static readonly Action<ProcessStartInfo> StartProcess = static startInfo =>
    {
        // Fire-and-forget: disposing the returned Process releases only the managed wrapper/handles — the launched
        // browser keeps running detached. (Process.Start may return null when an existing process is reused.)
        using var process = Process.Start(startInfo);
    };

    /// <summary>
    ///     Builds the OS-specific browser-open command. Returns the program to run and its single URL argument; never
    ///     uses a shell, so the URL is not interpreted by any command interpreter (repo convention: no
    ///     <see cref="ProcessStartInfo.UseShellExecute" /> = true).
    /// </summary>
    /// <param name="url">The loopback URL to open.</param>
    /// <param name="isWindows"><c>true</c> on Windows; otherwise the Linux command is used.</param>
    /// <returns>The file name (<c>explorer</c> / <c>xdg-open</c>) and the URL argument.</returns>
    internal static (string FileName, string Arguments) BuildOpenCommand(string url, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Windows: "explorer <url>" opens the default handler. NOTE: explorer.exe returns exit code 1 even on success,
        // so callers must never treat a non-zero exit as failure.
        // Linux: "xdg-open <url>" defers to the desktop's default browser.
        return isWindows
            ? ("explorer", url)
            : ("xdg-open", url);
    }

    /// <summary>
    ///     Launches the default browser at <paramref name="url" />. Any failure (missing <c>xdg-open</c>,
    ///     <see cref="Process.Start(ProcessStartInfo)" /> throwing) is swallowed and logged with the URL — it must never
    ///     abort startup. The <paramref name="launch" /> seam lets tests inject a throwing/recording launcher.
    /// </summary>
    internal static void OpenBrowser(string url, bool isWindows, ILogger logger, Action<ProcessStartInfo> launch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(launch);

        var (fileName, arguments) = BuildOpenCommand(url, isWindows);
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false
        };

        try
        {
            launch(startInfo);
            logger.LogInformation("Opened the default browser at {DesktopUrl}.", url);
        }
        catch (Exception exception)
        {
            // Non-fatal: the server still serves the SPA over loopback; surface the URL so the user can open it.
            logger.LogWarning(exception,
                "Could not open the default browser automatically. Open {DesktopUrl} manually to use the app.", url);
        }
    }
}
