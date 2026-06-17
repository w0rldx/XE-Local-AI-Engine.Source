namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>Shared constants for the llama-server provider.</summary>
public static class LlamaServerProviderConstants
{
    /// <summary>
    ///     Stable provider key used across persisted model selections, the per-model→provider map, and capability
    ///     payloads. Must match <see cref="ILocalModelProvider.ProviderName" /> of the llama-server provider.
    /// </summary>
    public const string ProviderName = "llamacpp";
}
