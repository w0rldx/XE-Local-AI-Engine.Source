namespace XE_Local_AI_Engine.Providers.Ollama;

/// <summary>
///     Startup registration values for the Ollama-backed local model provider.
/// </summary>
public sealed class OllamaLocalModelProviderRegistration
{
    /// <summary>Absolute Ollama API endpoint; local installs default to <c>http://127.0.0.1:11434</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Default chat model used when the caller selects the local runtime default.</summary>
    public required string Model { get; init; }
}
