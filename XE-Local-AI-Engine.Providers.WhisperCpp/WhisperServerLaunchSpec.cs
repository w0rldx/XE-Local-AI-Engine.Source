namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A fully-resolved launch specification for one <c>whisper-server</c> child process: the executable, the complete
///     ordered command-line argument vector, the allocated loopback port, and the working directory.
/// </summary>
/// <remarks>
///     Produced by the argument builder and consumed by the process launcher.
///     Internal on purpose. The launcher seam that takes it is internal too, and making either public would move it
///     under <c>PlacementConventionTests</c>' public-interface rule. Do not widen it for consistency with the public
///     contracts beside it.
/// </remarks>
internal sealed class WhisperServerLaunchSpec
{
    /// <summary>The catalogue id the process is being started for.</summary>
    public required string ModelId { get; init; }

    /// <summary>Absolute path to the resolved <c>whisper-server</c> executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The exact, ordered command-line argument vector.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The loopback port the process binds.</summary>
    public required int Port { get; init; }

    /// <summary>
    ///     The child's working directory — the binary's own directory, so co-located runtime libraries resolve through the
    ///     <c>$ORIGIN</c> RUNPATH on Linux and through DLL co-location on Windows.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The loopback server-root base URL every whisper-server route hangs off.</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
}
