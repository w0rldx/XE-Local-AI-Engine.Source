namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The single source of truth for the Ollama-runtime capability gate. The optional Ollama secondary runtime is
///     registered by <c>AddOllamaRuntime</c> only when <see cref="RuntimeEnabledConfigurationKey" /> is not explicitly
///     <c>false</c> (enabled by default). The running-models endpoint reads the SAME key so it can tell the client
///     whether Ollama is configured at all — a disabled runtime means the loaded-models page should stop polling rather
///     than back off forever against a runtime that will never answer.
/// </summary>
public static class OllamaRuntimeGate
{
    /// <summary>Config key gating the optional Ollama runtime. Enabled unless explicitly set to <c>false</c>.</summary>
    public const string RuntimeEnabledConfigurationKey = "XE_OLLAMA_RUNTIME_ENABLED";
}
