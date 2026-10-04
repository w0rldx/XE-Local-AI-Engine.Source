namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Live runtime facts for a currently-loaded local model — the effective context window the runtime actually
///     loaded, as opposed to the model's ADVERTISED train context.
/// </summary>
/// <remarks>
///     Provider-neutral; a provider that has no fixed launched window (Ollama, a not-yet-started model) reports
///     <see langword="null" /> instead.
/// </remarks>
public sealed class LocalModelRuntimeInfo
{
    /// <summary>The effective per-turn context window in tokens the running model was launched with.</summary>
    public required int EffectiveContextTokens { get; init; }

    /// <summary>Whether the running model's Mixture-of-Experts weights were placed in system RAM rather than on the GPU.</summary>
    public bool ExpertsOffloaded { get; init; }
}
