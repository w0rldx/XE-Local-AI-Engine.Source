namespace XE_Local_AI_Engine.Cli.Discovery;

using System.Diagnostics;
using System.Net;
using System.Text.Json;

/// <summary>Finds the node to attach to: an explicit <c>--url</c>, then <c>XE_URL</c>, then the engine's <c>ready.json</c>.</summary>
/// <remarks>
///     The <c>ready.json</c> rules mirror the Client's <c>DesktopPortStore</c> and the data directory mirrors
///     <c>DesktopBootstrap</c>; this project references neither, so both are restated here by hand.
/// </remarks>
internal static class NodeLocator
{
    internal const string UrlEnvironmentVariable = "XE_URL";

    internal const string DataDirectoryEnvironmentVariable = "XE_DATA_DIR";

    internal const string ReadyFileName = "ready.json";

    private const string ApplicationDataFolderName = "XE-Local-AI-Engine";

    private const int MaxReadyFileBytes = 64 * 1024;

    internal static Task<DiscoveredNode> LocateAsync(string? explicitUrl, CancellationToken cancellationToken) =>
        LocateAsync(explicitUrl,
            Environment.GetEnvironmentVariable(UrlEnvironmentVariable),
            static () => ResolveDataDirectory(Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            IsProcessAlive,
            cancellationToken);

    /// <param name="resolveDataDirectory">Called only when no URL names the node, so a bad <c>XE_DATA_DIR</c> never blocks an explicit URL.</param>
    internal static async Task<DiscoveredNode> LocateAsync(string? explicitUrl,
        string? environmentUrl,
        Func<string> resolveDataDirectory,
        Func<int, bool> isProcessAlive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveDataDirectory);
        ArgumentNullException.ThrowIfNull(isProcessAlive);

        var url = string.IsNullOrWhiteSpace(explicitUrl) ? environmentUrl : explicitUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            return new DiscoveredNode
            {
                Origin = ParseOriginOrNull(url, fromReadyFile: false)
                         ?? throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidUrl,
                             "The node URL must be an http or https loopback origin without credentials, a path, a query or a fragment.")
            };
        }

        var dataDirectory = resolveDataDirectory();
        var ready = await ReadReadyFileAsync(dataDirectory, cancellationToken);
        var origin = Validate(ready, dataDirectory)
                     ?? throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidReadyFile,
                         $"The engine's {ReadyFileName} in {dataDirectory} is not valid. Restart the engine.");
        if (!isProcessAlive(ready.Pid))
        {
            throw new NodeDiscoveryException(NodeDiscoveryFailure.StaleReadyFile,
                $"The engine that wrote {ReadyFileName} in {dataDirectory} is no longer running. Start the engine.");
        }

        return new DiscoveredNode
        {
            Origin = origin,
            Version = ready.Version,
            DataDirectory = dataDirectory,
            ProcessId = ready.Pid
        };
    }

    /// <summary>The engine's data directory: <c>XE_DATA_DIR</c> when set, else the per-user application-data folder.</summary>
    internal static string ResolveDataDirectory(string? overridden, string localApplicationData)
    {
        var path = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(localApplicationData, ApplicationDataFolderName)
            : overridden;
        return NormalizeAbsolutePathOrNull(path)
               ?? throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidDataDirectory,
                   $"The {DataDirectoryEnvironmentVariable} value must be a valid absolute path.");
    }

    /// <summary>
    ///     A canonical <c>scheme://host:port</c> origin, or null. An explicit URL may be http or https and name
    ///     <c>localhost</c>; a <c>ready.json</c> URL must be http on a loopback IP literal, as the engine writes it.
    /// </summary>
    internal static Uri? ParseOriginOrNull(string? value, bool fromReadyFile)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttp || (!fromReadyFile && url.Scheme == Uri.UriSchemeHttps))
            || url.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || url.AbsolutePath != "/"
            || !(fromReadyFile ? IsLoopbackAddress(url.Host) : IsLoopbackHost(url.Host)))
        {
            return null;
        }

        return new Uri(url.GetLeftPart(UriPartial.Authority));
    }

    internal static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IsLoopbackAddress(host);

    private static bool IsLoopbackAddress(string host) =>
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static async Task<ReadyFile> ReadReadyFileAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, ReadyFileName);
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > MaxReadyFileBytes)
            {
                throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidReadyFile, $"The engine's {ReadyFileName} in {dataDirectory} is too large.");
            }

            return await JsonSerializer.DeserializeAsync<ReadyFile>(stream, JsonSerializerOptions.Web, cancellationToken)
                   ?? throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidReadyFile, $"The engine's {ReadyFileName} in {dataDirectory} is empty.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new NodeDiscoveryException(NodeDiscoveryFailure.NotRunning,
                $"No running engine found for {dataDirectory}. Start the engine, or pass --url.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new NodeDiscoveryException(NodeDiscoveryFailure.InvalidReadyFile,
                $"The engine's {ReadyFileName} in {dataDirectory} could not be read: {exception.Message}");
        }
    }

    private static Uri? Validate(ReadyFile ready, string expectedDataDirectory)
    {
        if (ready.Pid <= 0
            || string.IsNullOrWhiteSpace(ready.Version)
            || ready.Version.Any(char.IsControl)
            || NormalizeAbsolutePathOrNull(ready.DataDir) is not { } recorded
            || !string.Equals(recorded,
                NormalizeAbsolutePathOrNull(expectedDataDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || ParseOriginOrNull(ready.Url, fromReadyFile: true) is not { } origin)
        {
            return null;
        }

        var canonical = origin.GetLeftPart(UriPartial.Authority);
        return string.Equals(ready.McpUrl, $"{canonical}/api/local/v1/mcp/server", StringComparison.Ordinal) ? origin : null;
    }

    private static string? NormalizeAbsolutePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
