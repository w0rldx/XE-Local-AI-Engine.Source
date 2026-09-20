namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The single source of truth for the Ollama-runtime capability gate.
/// </summary>
/// <remarks>
///     <c>AddOllamaRuntime</c> registers the optional Ollama secondary runtime only when
///     <see cref="RuntimeEnabledConfigurationKey" /> is not explicitly <c>false</c>, enabled being the default. The
///     running-models endpoint reads the SAME key, so it can tell the client whether Ollama is configured at all: a
///     disabled runtime means the loaded-models page stops polling rather than backing off forever against a runtime
///     that will never answer.
/// </remarks>
public static class OllamaRuntimeGate
{
    /// <summary>Config key gating the optional Ollama runtime. Enabled unless explicitly set to <c>false</c>.</summary>
    public const string RuntimeEnabledConfigurationKey = "XE_OLLAMA_RUNTIME_ENABLED";
}
