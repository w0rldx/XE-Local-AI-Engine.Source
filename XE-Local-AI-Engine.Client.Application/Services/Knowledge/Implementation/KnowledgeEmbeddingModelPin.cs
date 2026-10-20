namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>The node's own write of the knowledge-base embedding pin: write-once, never over an existing pin.</summary>
/// <remarks>
///     It goes straight to <see cref="INodeSettingsStore.UpdateAsync" />, under the store's lock, so two first index commits
///     racing each other keep the first pin. The operator changes a pin through the settings save, which validates it.
/// </remarks>
internal static class KnowledgeEmbeddingModelPin
{
    /// <summary>Pins <paramref name="modelName" /> when nothing is pinned yet; true when this call wrote the pin.</summary>
    public static async Task<bool> PinIfUnsetAsync(INodeSettingsStore store, string modelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var wrote = false;
        await store.UpdateAsync(latest =>
            {
                wrote = string.IsNullOrWhiteSpace(latest.KnowledgeEmbeddingModelName);
                return wrote
                    ? latest with
                    {
                        KnowledgeEmbeddingModelName = modelName.Trim()
                    }
                    : latest;
            },
            cancellationToken);
        return wrote;
    }
}
