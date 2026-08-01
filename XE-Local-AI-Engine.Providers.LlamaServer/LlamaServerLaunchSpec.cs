namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A fully-resolved launch specification for one <c>llama-server</c> child process: the executable, the complete
///     command-line argument list, the allocated localhost port, and the working directory. Produced by the supervisor
///     and consumed by <see cref="ILlamaServerProcessLauncher" />.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Arguments" /> is the exact, ordered argument vector handed to the process — it always binds
///         <c>--host 127.0.0.1</c>, a chat process always carries <c>--jinja</c> (tool calling), an
///         embedding process always carries <c>--embeddings</c> plus a non-<c>none</c> <c>--pooling</c> value, and a
///         reranker process always carries <c>--rerank</c> plus <c>--pooling rank</c> (mutually exclusive with
///         <c>--embeddings</c>) — all verified against llama.cpp release <c>b9692</c>. The spawn-args unit test asserts
///         these directly.
///     </para>
///     <para>
///         <see cref="WorkingDirectory" /> is the binary's own directory so that co-located runtime libraries (for
///         example the Windows CUDA <c>cudart-*</c> DLLs, when present alongside the server) resolve without polluting
///         the parent process environment.
///     </para>
/// </remarks>
/// <param name="ModelName">Model the process serves.</param>
/// <param name="Role">Role the process serves (chat vs embedding).</param>
/// <param name="ExecutablePath">Absolute path to the resolved <c>llama-server</c> executable.</param>
/// <param name="Arguments">The exact, ordered command-line argument vector.</param>
/// <param name="Port">The localhost port the process binds.</param>
/// <param name="WorkingDirectory">The working directory for the child (the binary's own directory).</param>
internal sealed record LlamaServerLaunchSpec(
    string ModelName,
    ModelRole Role,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int Port,
    string WorkingDirectory)
{
    /// <summary>The localhost OpenAI-compatible base URL the MEAI OpenAI adapter points at (ends with <c>/v1</c>).</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/v1");

    /// <summary>
    ///     Optional thread-safe sink invoked once per forwarded stdout/stderr line, IN ADDITION to logging — never
    ///     instead of it. Set only for operator profiling spawns that need diagnostic startup evidence; fitted replay
    ///     arguments are acquired separately through <c>llama-fit-params</c>. Both stdout and stderr invoke this
    ///     concurrently, so the supplier MUST use a thread-safe sink. Kept last with a default so existing construction
    ///     sites are unaffected.
    /// </summary>
    public Action<string>? StartupCapture { get; init; }

    /// <summary>
    ///     Optional predicate consulted per forwarded line to decide whether it is logged at Debug instead of
    ///     Information. <see langword="null" /> (every operator-driven spawn) means Information, exactly as before.
    /// </summary>
    /// <remarks>
    ///     Set only when the SUPERVISOR raised the child's log verbosity for its own measurement rather than because an
    ///     operator asked for verbose output. Those extra lines exist to be read in-process by the placement sniffer;
    ///     persisting them would multiply the serving log for a diagnostic nobody requested. The predicate stays false
    ///     until the process is serving, so the whole load window — including the layer-placement banner and every
    ///     failure message — is still logged at Information; only steady-state request chatter is demoted.
    /// </remarks>
    public Func<bool>? ShouldDemoteForwardedLines { get; init; }
}
