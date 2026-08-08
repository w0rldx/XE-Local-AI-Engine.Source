namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves a model's advertised <c>thinking</c>/<c>tools</c> capabilities, routed by the model's provider (Codex /
///     Azure Foundry declared matrices, a llama.cpp GGUF's offline chat-template detection, or an Ollama
///     <c>/api/show</c> classification). Extracted so the per-turn chat path and the per-participant orchestration path
///     resolve capabilities through ONE implementation rather than duplicating the provider-routing decision (which side
///     probes Ollama vs reads GGUF capabilities). A null/blank model or any detection miss resolves to NOT-capable for
///     both — the safe default that omits the think field (avoiding the Ollama 400) and withholds the tool offer.
/// </summary>
public interface IModelCapabilityResolver
{
    /// <summary>
    ///     Resolves <paramref name="model" />'s advertised thinking/tools capabilities AND its provider locality
    ///     (<c>IsCloud</c> = Codex OAuth / Azure Foundry; local otherwise). Cache-first; no probe on a cache hit. The
    ///     locality is resolved from the SAME provider-routing decision, so a caller gating on the EFFECTIVE (post-pin)
    ///     model gets both capability and locality from one lookup.
    /// </summary>
    Task<(bool SupportsThinking, bool SupportsTools, bool IsCloud)> ResolveAsync(string? model, CancellationToken cancellationToken);
}
