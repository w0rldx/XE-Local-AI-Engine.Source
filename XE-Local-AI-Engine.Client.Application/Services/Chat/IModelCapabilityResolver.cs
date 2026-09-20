namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves a model's advertised <c>thinking</c> and <c>tools</c> capabilities, routed by its provider: a declared
///     cloud matrix, a GGUF's offline chat-template detection, or an Ollama <c>/api/show</c> classification.
/// </summary>
/// <remarks>
///     The per-turn chat path and the per-participant orchestration path resolve through ONE implementation rather
///     than duplicating the provider-routing decision. A null or blank model, or any detection miss, resolves to
///     NOT-capable for both: the safe default that omits the think field, avoiding the Ollama 400, and withholds the
///     tool offer.
/// </remarks>
public interface IModelCapabilityResolver
{
    /// <summary>
    ///     Resolves <paramref name="model" />'s advertised thinking and tools capabilities AND its provider locality,
    ///     cache-first, with no probe on a hit.
    /// </summary>
    /// <remarks>
    ///     The locality comes from the SAME provider-routing decision, so a caller gating on the EFFECTIVE post-pin
    ///     model gets both capability and locality from one lookup.
    /// </remarks>
    Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken);
}
