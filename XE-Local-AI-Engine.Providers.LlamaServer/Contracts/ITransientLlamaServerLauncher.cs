namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A live, path-addressed llama-server for the duration of one body.</summary>
public sealed class TransientLlamaServerSession
{
    /// <summary>The localhost OpenAI-compatible base URL (ends with <c>/v1</c>).</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>
    ///     The id to send in a request body. A single-model server accepts any id, so this is the staged file's name —
    ///     the artifact has no registry identity yet, which is the whole reason this launcher exists.
    /// </summary>
    public required string ModelId { get; init; }
}

/// <summary>
///     Runs one throwaway <c>llama-server</c> against an explicit GGUF FILE PATH, optionally with a LoRA adapter
///     applied on top, and tears it down when the body returns.
/// </summary>
/// <remarks>
///     Every other spawn path here is addressed by installed-model NAME. A freshly exported training artifact is
///     staged and deliberately NOT in the registry, because a smoke test has to answer "would this file load and serve
///     at all" BEFORE anything is promoted, so it needs the one thing the supervisor cannot offer: a launch by path.
///     The process is owned entirely by this call — never registered, never counted against the cap, tree-killed on
///     every exit path — and wider exclusivity is the caller's (GPU load admission, the runtime-mutation lease).
/// </remarks>
public interface ITransientLlamaServerLauncher
{
    /// <exception cref="LlamaRuntimeException">The runtime could not be resolved, started, or did not become ready.</exception>
    Task<T> RunAsync<T>(TransientLlamaServerRequest request,
        Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
        CancellationToken ct);
}

public sealed class TransientLlamaServerRequest
{
    /// <summary>Absolute path to the GGUF llama-server loads as <c>-m</c>.</summary>
    public required string ModelFilePath { get; init; }

    /// <summary>Optional LoRA adapter applied on top as <c>--lora</c>.</summary>
    public required string? AdapterFilePath { get; init; }

    /// <summary>The context window to request. Kept small for a smoke load — this is not a serving spawn.</summary>
    public required int ContextTokens { get; init; }

    /// <summary>How long the load may take before the launch is abandoned.</summary>
    public required TimeSpan ReadinessTimeout { get; init; }
}
