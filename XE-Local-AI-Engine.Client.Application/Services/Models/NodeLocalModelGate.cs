namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The ONE predicate behind the adaptive-effort fast model's node-locality gate, so its save-time and its per-turn
///     enforcement point cannot answer differently.
/// </summary>
/// <remarks>
///     A model passes only when all three hold: it is present in the installed GGUF registry, its trust locality is
///     <see cref="ModelTrustLocality.Local" />, and it is served by the llama-server provider. <b>The installed check is the load-bearing
///     one</b> — both resolvers default an UNKNOWN id to "node-local llama.cpp", so on a node with no cloud provider configured the pair
///     admits EVERY string, a cloud model id included. Registry membership is what makes the gate mean what its name says; the other two
///     then refuse an installed GGUF an operator has since re-declared as external or remapped to Ollama.
/// </remarks>
internal static class NodeLocalModelGate
{
    /// <summary>Whether <paramref name="modelName" /> is an installed, node-local, llama-server-served model.</summary>
    /// <remarks>
    ///     <c>ModelTrustResolver</c> classifies a scheme-less id as <see cref="ModelTrustLocality.Local" /> whenever no cloud provider happens to be
    ///     selected for it, and <c>LocalModelProviderResolver</c> routes an unmapped id to the configured default provider, <c>llamacpp</c> — which is
    ///     why the registry check, not either resolver, is what makes this predicate mean what its name says.
    /// </remarks>
    public static async Task<bool> IsInstalledNodeLocalLlamaModelAsync(string? modelName,
        IGgufModelStore ggufModelStore,
        IModelTrustResolver modelTrustResolver,
        ILocalModelProviderResolver localModelProviderResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(localModelProviderResolver);

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        // The same canonical form the installed-model selection policy validates against, so a stored value with
        // incidental whitespace is judged as the registry stores it.
        var canonicalName = modelName.Trim();

        // Cheapest discriminator AND the one the other two cannot supply: an `ext:` id, a cloud id and a typo are all
        // absent from the registry, so they are refused before any resolver is consulted.
        if (!await ggufModelStore.ExistsAsync(canonicalName, cancellationToken))
        {
            return false;
        }

        if (await modelTrustResolver.ResolveAsync(canonicalName, cancellationToken) != ModelTrustLocality.Local)
        {
            return false;
        }

        var providerName = await localModelProviderResolver.ResolveProviderNameForModelAsync(canonicalName, cancellationToken);
        return string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
    }
}
