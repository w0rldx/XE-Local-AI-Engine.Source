namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using Microsoft.Extensions.AI;

/// <summary>
///     Keys under which the per-send sampling knobs that have no strongly-typed <see cref="ChatOptions" /> property
///     travel on <see cref="ChatOptions.AdditionalProperties" />.
/// </summary>
/// <remarks>
///     The literals are Ollama's raw option names (read by OllamaSharp 5.4.25's <c>AbstractionMapper</c> → <c>OllamaOption.*.Name</c>)
///     and — for the three sampling knobs — are also llama-server's own OpenAI-compatible body field names, so ONE entry serves both
///     runtimes: OllamaSharp maps it natively, and <c>DeferredLlamaServerChatClient.ApplySamplingPassthrough</c> patches it onto the
///     outbound chat-completions body. They live here, not private to the writer, because the producer
///     (<c>InvocationAgentFactory</c>, AI.Agent) and the llama.cpp consumer (Providers.LlamaServer) share only this assembly.
/// </remarks>
public static class SamplingOptionKeys
{
    /// <summary>Minimum token probability relative to the most likely token (0 = disabled). Honoured by both runtimes.</summary>
    public const string MinP = "min_p";

    /// <summary>Repetition penalty over the last <see cref="RepeatLastN" /> tokens (1.0 = disabled). Honoured by both runtimes.</summary>
    public const string RepeatPenalty = "repeat_penalty";

    /// <summary>How many trailing tokens the repetition penalty considers (0 = disabled, -1 = context size). Honoured by both runtimes.</summary>
    public const string RepeatLastN = "repeat_last_n";

    /// <summary>Ollama's per-request context window. Deliberately NOT patched onto the llama.cpp request.</summary>
    /// <remarks>
    ///     llama-server's window is fixed by the <c>--ctx-size</c> it was launched with and a per-request <c>n_ctx</c> is
    ///     not honoured, so on that runtime the value only drives the client-side history budget (<c>TurnPolicy</c>).
    /// </remarks>
    public const string NumCtx = "num_ctx";
}
