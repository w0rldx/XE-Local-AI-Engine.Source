namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILocalDefaultChatModelResolver" />, resolving the local-default chat model from the
///     installed GGUF (llama.cpp) models ONLY.
/// </summary>
/// <remarks>
///     Eligibility is <see cref="LocalGgufModelKindClassifier.IsChatModel" />, the rule the model picker reads, so the
///     default is always a model the picker lists. It reads only <see cref="IModelClassificationStore" />.
/// </remarks>
public sealed class LocalDefaultChatModelResolver : ILocalDefaultChatModelResolver
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IModelClassificationStore _modelClassificationStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public LocalDefaultChatModelResolver(IGgufModelStore ggufModelStore,
        IModelClassificationStore modelClassificationStore,
        ILlamaServerProcessSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(modelClassificationStore);
        ArgumentNullException.ThrowIfNull(supervisor);
        _ggufModelStore = ggufModelStore;
        _modelClassificationStore = modelClassificationStore;
        _supervisor = supervisor;
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

        // The operator overrides for every installed GGUF in one DB round-trip; the picker's own kind rule decides, so the two cannot disagree.
        var records = await _modelClassificationStore.ListAsync(cancellationToken);
        var classificationIndex = records.ToDictionary(static r => r.ModelName, static r => r, StringComparer.OrdinalIgnoreCase);

        var chatModels = named
                         .Where(descriptor => LocalGgufModelKindClassifier.IsChatModel(descriptor.ModelName,
                             classificationIndex.GetValueOrDefault(descriptor.ModelName),
                             descriptor.DecisionType))
                         .ToArray();
        if (chatModels.Length == 0)
        {
            return null;
        }

        // A resident Chat-role model wins: the persisted default if it is one of them, else the most recently used,
        // tie-broken by name. Only installed chat models count, so an embedding server or a deleted GGUF never does.
        var resident = _supervisor.ListRunningProcesses()
                                  .Where(static process => process.Role == ModelRole.Chat)
                                  .Select(process => new
                                  {
                                      Process = process,
                                      Model = FindByName(chatModels, process.ModelName)
                                  })
                                  .Where(static pair => pair.Model is not null)
                                  .ToArray();
        if (resident.Length > 0)
        {
            return (FindByName(resident.Select(static pair => pair.Model!), persistedDefault)
                    ?? resident.OrderByDescending(static pair => pair.Process.LastUsedUtc)
                               .ThenBy(static pair => pair.Model!.ModelName, StringComparer.OrdinalIgnoreCase)
                               .First()
                               .Model!).ModelName;
        }

        // The operator's persisted node default wins iff it is one of the installed GGUF chat models — short-circuits
        // the ordering scan and keeps the local default stable across sends.
        var match = FindByName(chatModels, persistedDefault);
        if (match is not null)
        {
            return match.ModelName;
        }

        // Deterministic fallback: most-recently-modified first, tie-break by name (case-insensitive).
        return chatModels
               .OrderByDescending(static descriptor => descriptor.ModifiedAt ?? DateTimeOffset.MinValue)
               .ThenBy(static descriptor => descriptor.ModelName, StringComparer.OrdinalIgnoreCase)
               .First()
               .ModelName;
    }

    private static LocalModelDescriptor? FindByName(IEnumerable<LocalModelDescriptor> models, string? modelName)
    {
        return string.IsNullOrWhiteSpace(modelName)
            ? null
            : models.FirstOrDefault(descriptor => string.Equals(descriptor.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
    }
}
