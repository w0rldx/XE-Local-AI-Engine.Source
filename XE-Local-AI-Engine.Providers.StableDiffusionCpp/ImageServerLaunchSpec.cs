namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A fully-resolved launch specification for one <c>sd-server</c> child process: the executable, the complete
///     command-line argument vector, the allocated loopback port, and the working directory.
/// </summary>
/// <remarks>
///     Produced by <see cref="Implementation.ImageServerArgumentBuilder" /> and consumed by the process launcher.
///     Mirrors <c>LlamaServerLaunchSpec</c>.
/// </remarks>
internal sealed class ImageServerLaunchSpec
{
    /// <summary>Model the process serves.</summary>
    public required string ModelName { get; init; }

    /// <summary>Absolute path to the resolved <c>sd-server</c> executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The exact, ordered command-line argument vector (host/port + model file-set + backend + threads).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The loopback port the process binds.</summary>
    public required int Port { get; init; }

    /// <summary>The working directory for the child (the binary's own directory, so co-located runtime libraries resolve).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The loopback server-root base URL the job client posts <c>/sdcpp/v1/…</c> routes against.</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
}
