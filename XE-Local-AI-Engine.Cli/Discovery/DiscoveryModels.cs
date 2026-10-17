namespace XE_Local_AI_Engine.Cli.Discovery;

/// <summary>The node the CLI attaches to: a validated loopback origin plus what <c>ready.json</c> said about it.</summary>
internal sealed class DiscoveredNode
{
    public required Uri Origin { get; init; }

    public string? Version { get; init; }

    public string? DataDirectory { get; init; }

    public int? ProcessId { get; init; }
}

/// <summary>The engine's <c>ready.json</c>, as the Client's <c>DesktopPortStore.PersistReady</c> writes it.</summary>
internal sealed class ReadyFile
{
    public string? Version { get; init; }

    public string? Url { get; init; }

    public string? McpUrl { get; init; }

    public string? DataDir { get; init; }

    public int Pid { get; init; }
}

/// <summary>Why no node could be located.</summary>
public enum NodeDiscoveryFailure
{
    /// <summary>The explicit <c>--url</c> or <c>XE_URL</c> value is not an http(s) loopback origin.</summary>
    InvalidUrl,

    /// <summary>The data directory from <c>XE_DATA_DIR</c> or the platform default is not a valid absolute path.</summary>
    InvalidDataDirectory,

    /// <summary>No <c>ready.json</c>: the engine is not running in this data directory.</summary>
    NotRunning,

    /// <summary><c>ready.json</c> exists but is unreadable or fails the engine's own validation rules.</summary>
    InvalidReadyFile,

    /// <summary><c>ready.json</c> names a process that is no longer running.</summary>
    StaleReadyFile
}
