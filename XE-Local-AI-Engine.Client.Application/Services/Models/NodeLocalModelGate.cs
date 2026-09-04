namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The ONE predicate behind the adaptive-effort fast model's node-locality gate, so its save-time and its
///     per-turn enforcement point cannot answer differently. A model passes only when all three hold: it is present
///     in the installed GGUF registry, its trust locality is
///     <see cref="ModelTrustLocality.Local" />, and it is served by the llama-server provider.
///     <para>
///         <b>The installed check is the load-bearing one.</b> Both resolvers below default an UNKNOWN id to
///         "node-local llama.cpp": <c>ModelTrustResolver</c> classifies a scheme-less id as
///         <see cref="ModelTrustLocality.Local" /> whenever no cloud provider happens to be selected for it, and
///         <c>LocalModelProviderResolver</c> routes an unmapped id to the configured default provider, which is
///         <c>llamacpp</c>. On a node with no cloud provider configured the pair therefore admits EVERY string,
///         a cloud model id included. Registry membership is what makes the gate mean what its name says; the other
///         two then refuse an installed GGUF that an operator has since re-declared as external or remapped to
///         Ollama.
///     </para>
/// </summary>
internal static class NodeLocalModelGate
{
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
        if (!await ggufModelStore.ExistsAsync(canonicalName, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await modelTrustResolver.ResolveAsync(canonicalName, cancellationToken).ConfigureAwait(false) != ModelTrustLocality.Local)
        {
            return false;
        }

        var providerName = await localModelProviderResolver.ResolveProviderNameForModelAsync(canonicalName, cancellationToken).ConfigureAwait(false);
        return string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
    }
}
