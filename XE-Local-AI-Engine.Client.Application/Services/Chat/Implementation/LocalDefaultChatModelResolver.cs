namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="ILocalDefaultChatModelResolver" />. Resolves the local-default chat model from the installed
///     GGUF (llama.cpp) models ONLY — never Ollama. An installed GGUF is chat-capable by construction; the only
///     exclusion is an entry whose PERSISTED effective kind (<c>OverrideKind ?? DetectedKind</c>) is
///     <see cref="ModelKind.Embedding" />. A model absent from the classifications table (no row) or whose effective
///     kind is Unknown or Chat stays eligible — exactly matching the chat picker's rule.
///     <para>
///         This resolver reads only from <see cref="IModelClassificationStore" /> (a plain DB read) and NEVER triggers
///         the Ollama <c>/api/show</c> detection probe. Passing <c>Digest=null</c> to
///         <c>IModelClassificationService.ClassifyAsync</c> would miss the cache and re-probe a now-dead Ollama on every
///         local-default send; using the store directly avoids that entirely.
///     </para>
/// </summary>
public sealed class LocalDefaultChatModelResolver(
    IGgufModelStore ggufModelStore,
    IModelClassificationStore modelClassificationStore) : ILocalDefaultChatModelResolver
{
    private readonly IGgufModelStore _ggufModelStore =
        ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));

    private readonly IModelClassificationStore _modelClassificationStore =
        modelClassificationStore ?? throw new ArgumentNullException(nameof(modelClassificationStore));

    public async Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);

        var named = installed
                    .Where(static descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName))
                    .ToArray();
        if (named.Length == 0)
        {
            return null;
        }

        // Read the persisted classifications for all installed GGUFs in one DB round-trip.
        // A missing row means unknown/unprobed → eligible. We exclude ONLY when the effective
        // kind (OverrideKind ?? DetectedKind) is explicitly Embedding.
        var records = await _modelClassificationStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var classificationIndex = records.ToDictionary(static r => r.ModelName, static r => r, StringComparer.OrdinalIgnoreCase);

        var chatModels = named
                         .Where(descriptor => !IsPersistedEmbedding(classificationIndex, descriptor.ModelName))
                         .ToArray();
        if (chatModels.Length == 0)
        {
            return null;
        }

        // The operator's persisted node default wins iff it is one of the installed GGUF chat models — short-circuits
        // the ordering scan and keeps the local default stable across sends.
        if (!string.IsNullOrWhiteSpace(persistedDefault))
        {
            var match = chatModels.FirstOrDefault(descriptor =>
                string.Equals(descriptor.ModelName, persistedDefault, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.ModelName;
            }
        }

        // Deterministic fallback: most-recently-modified first, tie-break by name (case-insensitive).
        return chatModels
               .OrderByDescending(static descriptor => descriptor.ModifiedAt ?? DateTimeOffset.MinValue)
               .ThenBy(static descriptor => descriptor.ModelName, StringComparer.OrdinalIgnoreCase)
               .First()
               .ModelName;
    }

    /// <summary>
    ///     Returns <c>true</c> when the PERSISTED effective kind (<c>OverrideKind ?? DetectedKind</c>) is
    ///     <see cref="ModelKind.Embedding" />. An absent row returns <c>false</c> (eligible).
    /// </summary>
    private static bool IsPersistedEmbedding(IReadOnlyDictionary<string, ModelClassificationRecord> index,
        string modelName)
    {
        if (!index.TryGetValue(modelName, out var record))
        {
            return false; // no row → Unknown → eligible
        }

        var effectiveKind = record.OverrideKind ?? record.DetectedKind;
        return effectiveKind == ModelKind.Embedding;
    }
}
