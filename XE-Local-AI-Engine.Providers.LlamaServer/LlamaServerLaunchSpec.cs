namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A fully-resolved launch specification for one <c>llama-server</c> child process: the executable, the complete
///     command-line argument list, the allocated localhost port, and the working directory. Produced by the supervisor
///     and consumed by <see cref="ILlamaServerProcessLauncher" />.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Arguments" /> is the exact, ordered argument vector handed to the process — it always binds
///         <c>--host 127.0.0.1</c> (decision #17), a chat process always carries <c>--jinja</c> (tool calling), and an
///         embedding process always carries <c>--embeddings</c> plus a non-<c>none</c> <c>--pooling</c> value
///         (verified against llama.cpp release <c>b9692</c>). The spawn-args unit test asserts these directly.
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
}
