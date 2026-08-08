namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     <see cref="IGgufModelCapabilityResolver" /> over <see cref="IGgufModelStore" />: matches the requested model name
///     against the installed GGUF descriptors (whose <c>IsToolCapable</c>/<c>IsReasoningCapable</c> were detected from
///     the chat template and cached per file) and surfaces the thinking/tools flags. A name that matches no installed
///     GGUF returns <see langword="null" /> so the caller falls back to the Ollama/Codex capability path.
/// </summary>
internal sealed class GgufModelCapabilityResolver(IGgufModelStore ggufModelStore) : IGgufModelCapabilityResolver
{
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));

    /// <inheritdoc />
    public async Task<GgufModelCapabilities?> TryResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        // The list reuses the store's per-file header cache, so this never re-reads a model file on a cache hit.
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);

        var descriptor = installed.FirstOrDefault(model =>
            string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return null;
        }

        return new GgufModelCapabilities(descriptor.IsReasoningCapable, descriptor.IsToolCapable);
    }
}
