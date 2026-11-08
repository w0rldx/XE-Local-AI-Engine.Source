namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Publishes the desktop readiness contract: the persisted port, <c>ready.json</c> and the <c>XE_READY=1</c> stdout
///     line the shell and <c>--status</c> wait for.
/// </summary>
/// <remarks>
///     Shared by <see cref="DesktopLifecycle" /> (the real host) and the locked vault pre-host, so both announce
///     themselves byte-identically on the same origin.
/// </remarks>
internal static class DesktopReadyPublisher
{
    internal static void Publish(string dataDirectory,
        string url,
        string version,
        TimeProvider timeProvider,
        TextWriter standardOutput,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var boundUri))
        {
            return;
        }

        DesktopPortStore.Persist(dataDirectory, boundUri.Port, logger);
        var canonicalUrl = url.TrimEnd('/');
        var mcpUrl = $"{canonicalUrl}/api/local/v1/mcp/server";
        DesktopPortStore.PersistReady(dataDirectory,
            new ReadyInfo(version, canonicalUrl, mcpUrl, dataDirectory, Environment.ProcessId, timeProvider.GetUtcNow()),
            logger);
#pragma warning disable MA0045 // Called from the synchronous ApplicationStarted callback.
        standardOutput.WriteLine($"XE_READY=1 XE_VERSION={version} XE_URL={canonicalUrl} XE_MCP_URL={mcpUrl} XE_DATA_DIR={dataDirectory}");
#pragma warning restore MA0045
    }

    /// <summary>
    ///     Tells the person at the terminal where a browser-mode engine is listening. Separate from the machine-read
    ///     <c>XE_READY</c> line, whose exact bytes are a contract.
    /// </summary>
    internal static void WriteBrowserBanner(string url, TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        var banner = string.Join(Environment.NewLine,
            string.Empty,
            "  XE Local AI Engine is ready",
            $"  Open in your browser: {url}",
            "  Press Ctrl+C to stop",
            string.Empty);
#pragma warning disable MA0045 // Called from the synchronous ApplicationStarted callback.
        standardOutput.WriteLine(banner);
#pragma warning restore MA0045
    }
}
