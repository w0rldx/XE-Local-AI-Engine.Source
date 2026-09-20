namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     <see cref="IGgufModelCapabilityResolver" /> over <see cref="IGgufModelStore" />, matching the requested name
///     against the installed GGUF descriptors and surfacing their thinking and tools flags.
/// </summary>
/// <remarks>
///     Those descriptor flags were detected from the chat template and cached per file. A name that matches no
///     installed GGUF returns <see langword="null" />, so the caller falls back to the Ollama or Codex path.
/// </remarks>
internal sealed class GgufModelCapabilityResolver : IGgufModelCapabilityResolver
{
    private readonly IGgufModelStore _ggufModelStore;

    public GgufModelCapabilityResolver(IGgufModelStore ggufModelStore)
    {
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        _ggufModelStore = ggufModelStore;
    }

    /// <inheritdoc />
    public async Task<GgufModelCapabilities?> TryResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        // The list reuses the store's per-file header cache, so this never re-reads a model file on a cache hit.
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);

        var descriptor = installed.FirstOrDefault(model =>
            string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return null;
        }

        // Vision comes from IsMultimodalCapable, true only with the mmproj companion that gates the --mmproj launch,
        // and the reasoning-budget flag rides the same descriptor, so neither claims what the runtime cannot serve.
        return new GgufModelCapabilities(descriptor.IsReasoningCapable,
            descriptor.IsToolCapable,
            descriptor.IsMultimodalCapable,
            descriptor.ReasoningBudgetEnforceable);
    }
}
