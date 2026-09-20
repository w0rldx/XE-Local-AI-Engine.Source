namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="ILocalDefaultChatModelResolver" />, resolving the local-default chat model from the
///     installed GGUF (llama.cpp) models ONLY — never Ollama.
/// </summary>
/// <remarks>
///     An installed GGUF is chat-capable by construction. A model is excluded when its persisted effective kind
///     (<c>OverrideKind ?? DetectedKind</c>) is <see cref="ModelKind.Embedding" /> or
///     <see cref="ModelKind.Reranker" />, or — belt and braces, since a fresh GGUF has no row — when its NAME matches
///     one, which an explicit operator override outranks. It reads only <see cref="IModelClassificationStore" />,
///     never the Ollama probe <c>ClassifyAsync</c> would re-run against a dead daemon on every local-default send.
/// </remarks>
public sealed class LocalDefaultChatModelResolver : ILocalDefaultChatModelResolver
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IModelClassificationStore _modelClassificationStore;

    public LocalDefaultChatModelResolver(
        IGgufModelStore ggufModelStore,
        IModelClassificationStore modelClassificationStore)
    {
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(modelClassificationStore);
        _ggufModelStore = ggufModelStore;
        _modelClassificationStore = modelClassificationStore;
    }

    public async Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);

        var named = installed
                    .Where(static descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName))
                    .ToArray();
        if (named.Length == 0)
        {
            return null;
        }

        // The persisted classifications for every installed GGUF in one DB round-trip. A missing row means unprobed
        // and therefore eligible; only an explicitly non-chat effective kind excludes a model.
        var records = await _modelClassificationStore.ListAsync(cancellationToken);
        var classificationIndex = records.ToDictionary(static r => r.ModelName, static r => r, StringComparer.OrdinalIgnoreCase);

        var chatModels = named
                         .Where(descriptor => !IsExcludedFromChat(classificationIndex, descriptor.ModelName))
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
    ///     Whether the model must be excluded from the local-default chat pick.
    /// </summary>
    /// <remarks>
    ///     It is excluded when its persisted effective kind is <see cref="ModelKind.Embedding" /> or
    ///     <see cref="ModelKind.Reranker" />, or when its name identifies one and the operator did not override the
    ///     kind to a chat-eligible one — so a corrected model stays eligible.
    /// </remarks>
    private static bool IsExcludedFromChat(IReadOnlyDictionary<string, ModelClassificationRecord> index,
        string modelName)
    {
        // A speculative-decoding drafter is structurally excluded, ahead of every override: it is a partial model that
        // only runs inside another process's --spec-model slot, so no operator override can make it chat-eligible.
        if (ModelKindDetector.IsDraftName(modelName))
        {
            return true;
        }

        if (IsPersistedNonChat(index, modelName))
        {
            return true;
        }

        // An explicit operator override to a chat-eligible kind (for example correcting a misdetected embedding GGUF to
        // Chat) is authoritative — do not second-guess it with the name heuristic.
        if (HasExplicitChatEligibleOverride(index, modelName))
        {
            return false;
        }

        return ModelKindDetector.IsEmbeddingName(modelName) || ModelKindDetector.IsRerankerName(modelName);
    }

    /// <summary>
    ///     Returns <c>true</c> when the PERSISTED effective kind (<c>OverrideKind ?? DetectedKind</c>) is a non-chat kind
    ///     (<see cref="ModelKind.Embedding" /> or <see cref="ModelKind.Reranker" />). An absent row returns <c>false</c>
    ///     (eligible).
    /// </summary>
    private static bool IsPersistedNonChat(IReadOnlyDictionary<string, ModelClassificationRecord> index,
        string modelName)
    {
        if (!index.TryGetValue(modelName, out var record))
        {
            return false; // no row → Unknown → eligible
        }

        var effectiveKind = record.OverrideKind ?? record.DetectedKind;
        return effectiveKind is ModelKind.Embedding or ModelKind.Reranker or ModelKind.Draft;
    }

    /// <summary>
    ///     Returns <c>true</c> when the operator explicitly overrode the kind to a chat-eligible value
    ///     (<c>OverrideKind</c> is set and is neither <see cref="ModelKind.Embedding" /> nor
    ///     <see cref="ModelKind.Reranker" />). Only an explicit override — not an auto-detected kind — suppresses the name
    ///     heuristic.
    /// </summary>
    private static bool HasExplicitChatEligibleOverride(IReadOnlyDictionary<string, ModelClassificationRecord> index,
        string modelName)
    {
        return index.TryGetValue(modelName, out var record)
               && record.OverrideKind is { } overrideKind
               && overrideKind is not (ModelKind.Embedding or ModelKind.Reranker or ModelKind.Draft);
    }
}
