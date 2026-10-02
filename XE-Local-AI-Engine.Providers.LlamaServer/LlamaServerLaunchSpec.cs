namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A fully-resolved launch specification for one <c>llama-server</c> child process: the executable, the complete
///     command-line argument vector, the allocated localhost port and the working directory.
/// </summary>
/// <remarks>
///     Produced by the supervisor for <see cref="Contracts.ILlamaServerProcessLauncher" />. <see cref="Arguments" /> is the
///     exact, ordered vector handed to the process and always binds <c>--host 127.0.0.1</c>; its per-role flags are
///     asserted by the spawn-args unit test and set out in docs/wiki/03-local-runtime-and-providers.md, "Per-role
///     launch flags and the pooled batch-size rule". <see cref="WorkingDirectory" /> is the binary's own directory, so
///     co-located runtime libraries resolve without polluting the parent process environment.
/// </remarks>
internal sealed record LlamaServerLaunchSpec
{
    /// <summary>Model the process serves.</summary>
    public required string ModelName { get; init; }

    /// <summary>Role the process serves (chat vs embedding).</summary>
    public required ModelRole Role { get; init; }

    /// <summary>Absolute path to the resolved <c>llama-server</c> executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The exact, ordered command-line argument vector.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The localhost port the process binds.</summary>
    public required int Port { get; init; }

    /// <summary>The working directory for the child (the binary's own directory).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The localhost OpenAI-compatible base URL the MEAI OpenAI adapter points at (ends with <c>/v1</c>).</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/v1");

    /// <summary>
    ///     Optional sink invoked once per forwarded stdout/stderr line, IN ADDITION to logging and never instead of it.
    ///     Both streams invoke it concurrently, so the supplier MUST be thread-safe.
    /// </summary>
    /// <remarks>
    ///     Set only for operator profiling spawns that need diagnostic startup evidence; fitted replay arguments are
    ///     acquired separately through <c>llama-fit-params</c>.
    /// </remarks>
    public Action<string>? StartupCapture { get; init; }
}
