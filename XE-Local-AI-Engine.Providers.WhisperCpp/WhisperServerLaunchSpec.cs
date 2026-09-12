namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A fully-resolved launch specification for one <c>whisper-server</c> child process: the executable, the complete
///     ordered command-line argument vector, the allocated loopback port, and the working directory. Produced by the
///     argument builder and consumed by the process launcher.
/// </summary>
/// <remarks>
///     Internal on purpose. The launcher seam that takes it is internal too, and making either public would move it
///     under <c>PlacementConventionTests</c>' public-interface rule. Do not widen it for consistency with the public
///     contracts beside it.
/// </remarks>
/// <param name="ModelId">The catalogue id the process is being started for.</param>
/// <param name="ExecutablePath">Absolute path to the resolved <c>whisper-server</c> executable.</param>
/// <param name="Arguments">The exact, ordered command-line argument vector.</param>
/// <param name="Port">The loopback port the process binds.</param>
/// <param name="WorkingDirectory">
///     The child's working directory — the binary's own directory, so co-located runtime libraries resolve through the
///     <c>$ORIGIN</c> RUNPATH on Linux and through DLL co-location on Windows.
/// </param>
internal sealed record WhisperServerLaunchSpec(
    string ModelId,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int Port,
    string WorkingDirectory)
{
    /// <summary>The loopback server-root base URL every whisper-server route hangs off.</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
}
