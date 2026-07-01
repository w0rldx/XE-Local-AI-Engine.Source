namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A fully-resolved launch specification for one <c>sd-server</c> child process: the executable, the complete
///     command-line argument vector, the allocated loopback port, and the working directory. Produced by
///     <see cref="Implementation.ImageServerArgumentBuilder" /> and consumed by the process launcher. Mirrors
///     <c>LlamaServerLaunchSpec</c>.
/// </summary>
/// <param name="ModelName">Model the process serves.</param>
/// <param name="ExecutablePath">Absolute path to the resolved <c>sd-server</c> executable.</param>
/// <param name="Arguments">The exact, ordered command-line argument vector (host/port + model file-set + backend + threads).</param>
/// <param name="Port">The loopback port the process binds.</param>
/// <param name="WorkingDirectory">The working directory for the child (the binary's own directory, so co-located runtime libraries resolve).</param>
internal sealed record ImageServerLaunchSpec(
    string ModelName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int Port,
    string WorkingDirectory)
{
    /// <summary>The loopback server-root base URL the job client posts <c>/sdcpp/v1/…</c> routes against.</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
}
