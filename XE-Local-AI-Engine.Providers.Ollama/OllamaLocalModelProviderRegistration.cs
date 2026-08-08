namespace XE_Local_AI_Engine.Providers.Ollama;

/// <summary>
///     Startup registration values for the Ollama-backed local model provider.
/// </summary>
/// <param name="Endpoint">Absolute Ollama API endpoint; local installs default to <c>http://127.0.0.1:11434</c>.</param>
/// <param name="Model">Default chat model used when the caller selects the local runtime default.</param>
public sealed record OllamaLocalModelProviderRegistration(Uri Endpoint, string Model);
