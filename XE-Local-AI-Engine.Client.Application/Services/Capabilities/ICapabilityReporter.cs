namespace XE_Local_AI_Engine.Client.Services.Capabilities;

/// <summary>
///     Capability preflight for the optional Ollama runtime. This used to also compose and push a full node
///     capability report; that report had exactly one consumer, the outbound Central Platform hub, and went with it.
/// </summary>
public interface ICapabilityReporter
{
    /// <summary>
    ///     True when the Ollama runtime is reachable and can serve <paramref name="modelName" /> — either because it
    ///     is installed, or because the node's effective default model is installed and can stand in for it.
    /// </summary>
    Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default);
}
