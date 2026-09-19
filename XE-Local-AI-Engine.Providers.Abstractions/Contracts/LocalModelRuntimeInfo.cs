namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Live runtime facts for a currently-loaded local model — the effective context window the runtime actually loaded
///     (as opposed to the model's ADVERTISED train context). Provider-neutral; a provider that has no fixed launched
///     window (Ollama, a not-yet-started model) reports <see langword="null" /> instead.
/// </summary>
public sealed class LocalModelRuntimeInfo
{
    /// <summary>The effective per-turn context window in tokens the running model was launched with.</summary>
    public required int EffectiveContextTokens { get; init; }
}
