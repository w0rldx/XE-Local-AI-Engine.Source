namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;
using System.Net;
using System.Net.Sockets;

/// <summary>
///     Persists and reuses the loopback port a desktop launch binds. Binding <c>http://127.0.0.1:0</c> lets the OS pick a
///     NEW free port every launch, which changes the browser origin (scheme+host+port) and silently resets every
///     <c>localStorage</c>-backed user preference between runs. Remembering the last bound port and re-binding it keeps
///     the origin stable so preferences survive; if the remembered port is gone (taken or invalid), the launch falls back
///     to <c>:0</c> and the newly assigned port is persisted instead.
///     <para>
///         Strictly desktop-mode-only: every member is reached solely behind the <c>XE_LAUNCH_MODE=desktop</c> /
///         <c>--desktop</c> branch, so headless/Aspire/CI runs never touch the port file and keep their byte-identical
///         behavior. Reads are best-effort — any failure resolves to the dynamic <c>:0</c> bind rather than throwing.
///     </para>
/// </summary>
internal static class DesktopPortStore
{
    /// <summary>The per-user data-directory file name that records the last bound loopback port (plain text, not a secret).</summary>
    internal const string PortFileName = "desktop-port.txt";

    /// <summary>Ports at or below this are well-known/privileged; a desktop loopback bind never legitimately uses one.</summary>
    private const int MinimumDynamicPort = 1025;

    /// <summary>The maximum valid TCP port number.</summary>
    private const int MaximumPort = 65535;

    /// <summary>
    ///     Resolves the URL desktop mode should bind: the remembered port (<c>http://127.0.0.1:{port}</c>) when a valid,
    ///     currently-free port was persisted, otherwise the dynamic <see cref="DesktopLaunch.LoopbackBindUrl" /> (<c>:0</c>).
    ///     Never throws — any IO / parse / availability failure resolves to the dynamic bind.
    /// </summary>
    /// <param name="dataDirectory">The per-user data directory that holds the port file.</param>
    internal static string ResolveBindUrl(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        var port = TryReadPersistedPort(dataDirectory);
        if (port is null)
        {
            return DesktopLaunch.LoopbackBindUrl;
        }

        if (!IsPortAvailable(port.Value))
        {
            return DesktopLaunch.LoopbackBindUrl;
        }

#pragma warning disable S5332 // Desktop mode deliberately binds plain http on 127.0.0.1 (same as DesktopLaunch.LoopbackBindUrl): traffic never leaves the machine and localhost has no certificate story for a self-contained desktop app.
        return $"http://{DesktopLaunch.LoopbackHost}:{port.Value.ToString(CultureInfo.InvariantCulture)}";
#pragma warning restore S5332
    }

    /// <summary>
    ///     Persists the actually-bound loopback port so the next launch can re-bind it. Best-effort and non-fatal: a write
    ///     failure is logged at Warning and swallowed (the next launch simply falls back to a dynamic port). Writes via a
    ///     temp file + move so a crash mid-write can never leave a torn port file.
    /// </summary>
    /// <param name="dataDirectory">The per-user data directory that holds the port file.</param>
    /// <param name="port">The loopback port Kestrel actually bound.</param>
    /// <param name="logger">Logs a non-fatal write failure with the target path for diagnostics.</param>
    internal static void Persist(string dataDirectory, int port, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var portFilePath = Path.Combine(dataDirectory, PortFileName);
        var tempPath = portFilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, port.ToString(CultureInfo.InvariantCulture));
            File.Move(tempPath, portFilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: failing to remember the port only costs a fresh dynamic port (and a prefs reset) next launch.
            logger.LogWarning(exception, "Could not persist the desktop loopback port to {PortFilePath}.", portFilePath);

            // A write that succeeded but whose move failed leaves the temp file behind; clear it so it never lingers.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a stale temp file is harmless (never read; overwritten on the next successful persist).
            }
        }
    }

    private static int? TryReadPersistedPort(string dataDirectory)
    {
        var portFilePath = Path.Combine(dataDirectory, PortFileName);

        string content;
        try
        {
            if (!File.Exists(portFilePath))
            {
                return null;
            }

            content = File.ReadAllText(portFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!int.TryParse(content.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        if (port < MinimumDynamicPort || port > MaximumPort)
        {
            return null;
        }

        return port;
    }

    private static bool IsPortAvailable(int port)
    {
        // Probe by binding a throwaway loopback listener: if the OS rejects it, the port is already taken so we must fall
        // back to a dynamic port. The listener is stopped before Kestrel binds, leaving a tiny TOCTOU window in which
        // another process could grab the port between this probe and Kestrel's bind. Acceptable for a single-user
        // loopback launch: in that rare case Kestrel would fail fast on startup — the same failure mode as today when a
        // chosen port is unavailable — so this stays best-effort rather than holding the socket across the gap.
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
