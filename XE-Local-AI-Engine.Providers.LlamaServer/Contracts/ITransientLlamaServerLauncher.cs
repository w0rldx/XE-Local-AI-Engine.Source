namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A live, path-addressed llama-server for the duration of one body.</summary>
/// <param name="BaseAddress">The localhost OpenAI-compatible base URL (ends with <c>/v1</c>).</param>
/// <param name="ModelId">
///     The id to send in a request body. A single-model server accepts any id, so this is the staged file's name —
///     the artifact has no registry identity yet, which is the whole reason this launcher exists.
/// </param>
public sealed record TransientLlamaServerSession(Uri BaseAddress, string ModelId);

/// <summary>
///     Runs one throwaway <c>llama-server</c> against an explicit GGUF FILE PATH, optionally with a LoRA adapter
///     applied on top, and tears it down when the body returns.
/// </summary>
/// <remarks>
///     <para>
///         Every other spawn path in this provider is addressed by installed-model NAME and resolves its file through
///         the registry. A freshly exported training artifact is staged and deliberately NOT in the registry — a smoke
///         test has to answer "would this file load and serve at all" BEFORE anything gets promoted, so it needs the
///         one thing the supervisor cannot offer: a launch by path.
///     </para>
///     <para>
///         The process is owned entirely by this call: it is never registered in the supervisor's process table, never
///         counts against the loaded cap, and is tree-killed on every exit path including a throw. Callers are
///         responsible for whatever wider exclusivity they need (GPU load admission, the runtime-mutation lease).
///     </para>
/// </remarks>
public interface ITransientLlamaServerLauncher
{
    /// <exception cref="LlamaRuntimeException">The runtime could not be resolved, started, or did not become ready.</exception>
    Task<T> RunAsync<T>(TransientLlamaServerRequest request,
        Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
        CancellationToken ct);
}

/// <param name="ModelFilePath">Absolute path to the GGUF llama-server loads as <c>-m</c>.</param>
/// <param name="AdapterFilePath">Optional LoRA adapter applied on top as <c>--lora</c>.</param>
/// <param name="ContextTokens">The context window to request. Kept small for a smoke load — this is not a serving spawn.</param>
/// <param name="ReadinessTimeout">How long the load may take before the launch is abandoned.</param>
public sealed record TransientLlamaServerRequest(
    string ModelFilePath,
    string? AdapterFilePath,
    int ContextTokens,
    TimeSpan ReadinessTimeout);
