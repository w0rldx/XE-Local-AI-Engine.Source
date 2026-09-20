namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>
///     Persists and reuses the loopback port a desktop launch binds.
/// </summary>
/// <remarks>
///     Binding <c>http://127.0.0.1:0</c> lets the OS pick a NEW free port every launch, which changes the browser
///     origin (scheme+host+port) and silently resets every <c>localStorage</c>-backed user preference between runs.
///     Re-binding the remembered port keeps the origin stable, and a port that is gone or invalid resolves to <c>:0</c>
///     with the newly assigned port persisted instead. Strictly local-mode-only, so headless/Aspire/CI never touch the
///     port file, and reads are best-effort: any failure resolves to the dynamic bind rather than throwing.
/// </remarks>
internal static class DesktopPortStore
{
    /// <summary>The per-user data-directory file name that records the last bound loopback port (plain text, not a secret).</summary>
    internal const string PortFileName = "desktop-port.txt";

    internal const string ReadyFileName = "ready.json";

    /// <summary>Ports at or below this are well-known/privileged; a desktop loopback bind never legitimately uses one.</summary>
    private const int MinimumDynamicPort = 1025;

    /// <summary>The maximum valid TCP port number.</summary>
    private const int MaximumPort = 65535;

    /// <summary>
    ///     Resolves the URL desktop mode should bind: the remembered port (<c>http://127.0.0.1:{port}</c>) when a
    ///     valid, currently-free one was persisted, otherwise <see cref="DesktopLaunch.LoopbackBindUrl" /> (<c>:0</c>).
    /// </summary>
    /// <remarks>
    ///     Never throws — any IO, parse or availability failure resolves to the dynamic bind.
    /// </remarks>
    /// <param name="dataDirectory">The per-user data directory that holds the port file.</param>
    internal static async Task<string> ResolveBindUrlAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);

        var port = await TryReadPersistedPortAsync(dataDirectory, cancellationToken);
        if (port is null)
        {
            return DesktopLaunch.LoopbackBindUrl;
        }

        if (!IsPortAvailable(port.Value))
        {
            return DesktopLaunch.LoopbackBindUrl;
        }

#pragma warning disable S5332 // Desktop mode deliberately binds plain http on 127.0.0.1 (same as DesktopLaunch.LoopbackBindUrl): traffic never leaves the machine and localhost has no certificate story for a packaged desktop app.
        return $"http://{DesktopLaunch.LoopbackHost}:{port.Value.ToString(CultureInfo.InvariantCulture)}";
#pragma warning restore S5332
    }

    /// <summary>
    ///     Persists the actually-bound loopback port so the next launch can re-bind it.
    /// </summary>
    /// <remarks>
    ///     Best-effort and non-fatal: a write failure is logged at Warning and swallowed, and the next launch resolves
    ///     to a dynamic port. Writes via a temp file + move, so a crash mid-write can never leave a torn port file.
    /// </remarks>
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
#pragma warning disable MA0045 // Reached only from DesktopLifecycle.OnApplicationStarted, the synchronous Action registered on IHostApplicationLifetime.ApplicationStarted.
            File.WriteAllText(tempPath, port.ToString(CultureInfo.InvariantCulture));
#pragma warning restore MA0045
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

    internal static void PersistReady(string dataDirectory, ReadyInfo info, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(logger);

        var path = Path.Combine(dataDirectory, ReadyFileName);
        var tempPath = path + ".tmp";
        try
        {
#pragma warning disable MA0045 // Reached only from DesktopLifecycle.OnApplicationStarted, the synchronous Action registered on IHostApplicationLifetime.ApplicationStarted.
            File.WriteAllText(tempPath, JsonSerializer.Serialize(info, JsonSerializerOptions.Web));
#pragma warning restore MA0045
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not persist engine readiness to {ReadyFilePath}.", path);
            TryDelete(tempPath);
        }
    }

    internal static async Task<ReadyInfo?> ReadReadyAsync(string dataDirectory, CancellationToken cancellationToken = default) =>
        (await ReadReadyEvidenceAsync(dataDirectory, cancellationToken)).Info;

    internal static async Task<ReadyEvidence> ReadReadyEvidenceAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        var path = Path.Combine(dataDirectory, ReadyFileName);
        try
        {
            await using var stream = File.OpenRead(path);
            var parsed = await JsonSerializer.DeserializeAsync<ReadyInfo>(stream, JsonSerializerOptions.Web, cancellationToken);
            return TryValidateReadyInfo(parsed, dataDirectory, out var validated)
                ? new ReadyEvidence { State = ReadyEvidenceState.Valid, Info = validated }
                : new ReadyEvidence { State = ReadyEvidenceState.Invalid, Info = null };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadyEvidence { State = ReadyEvidenceState.Absent, Info = null };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReadyEvidence { State = ReadyEvidenceState.Invalid, Info = null };
        }
    }

    private static bool TryValidateReadyInfo(ReadyInfo? info, string expectedDataDirectory, out ReadyInfo? validated)
    {
        validated = null;
        if (info is null
            || info.Pid <= 0
            || string.IsNullOrWhiteSpace(info.Version)
            || info.Version.Any(char.IsControl)
            || !TryNormalizeAbsolutePath(info.DataDir, out var recordedDataDirectory)
            || !TryNormalizeAbsolutePath(expectedDataDirectory, out var normalizedExpectedDirectory)
            || !ReadyDataDirectoriesEqual(recordedDataDirectory, normalizedExpectedDirectory, OperatingSystem.IsWindows())
            || !Uri.TryCreate(info.Url, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttp
            || url.Port is < 1 or > MaximumPort
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || url.AbsolutePath != "/"
            || !IPAddress.TryParse(url.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            return false;
        }

        var canonicalUrl = url.GetLeftPart(UriPartial.Authority);
        var canonicalMcpUrl = $"{canonicalUrl}/api/local/v1/mcp/server";
        if (!string.Equals(info.McpUrl, canonicalMcpUrl, StringComparison.Ordinal))
        {
            return false;
        }

        validated = info with
        {
            Url = canonicalUrl,
            McpUrl = canonicalMcpUrl,
            DataDir = normalizedExpectedDirectory
        };
        return true;
    }

    private static bool TryNormalizeAbsolutePath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool ReadyDataDirectoriesEqual(string recorded, string expected, bool isWindows) =>
        string.Equals(recorded,
            expected,
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void DeleteReady(string dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        var path = Path.Combine(dataDirectory, ReadyFileName);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove stale engine readiness file {ReadyFilePath}.", path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; temporary files are never read.
        }
    }

    private static async Task<int?> TryReadPersistedPortAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var portFilePath = Path.Combine(dataDirectory, PortFileName);

        string content;
        try
        {
            if (!File.Exists(portFilePath))
            {
                return null;
            }

            content = await File.ReadAllTextAsync(portFilePath, cancellationToken);
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

    internal static bool IsPortAvailable(int port)
    {
        // Probe by binding a throwaway loopback listener; rejection means the port is taken and a dynamic one is used. The listener stops before Kestrel binds, leaving a TOCTOU window.
        // Best-effort rather than holding the socket across that gap: if another process wins it, Kestrel fails fast exactly as for any unavailable chosen port.
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

internal sealed record ReadyInfo(string Version, string Url, string McpUrl, string DataDir, int Pid, DateTimeOffset StartedAtUtc);

internal enum ReadyEvidenceState
{
    Absent,
    Valid,
    Invalid
}

internal sealed class ReadyEvidence
{
    public required ReadyEvidenceState State { get; init; }

    public required ReadyInfo? Info { get; init; }
}
