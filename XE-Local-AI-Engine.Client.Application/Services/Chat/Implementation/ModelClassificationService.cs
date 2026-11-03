namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Writes the operator's kind override and reports the kind it leaves the model with.</summary>
/// <remarks>
///     The rule itself is <see cref="LocalGgufModelKindClassifier.ResolveKind" />, shared with the list mapper and
///     every eligibility gate, so the response of an override can never disagree with the next list.
/// </remarks>
internal sealed class ModelClassificationService : IModelClassificationService
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IModelClassificationStore _store;

    public ModelClassificationService(IModelClassificationStore store, IGgufModelStore ggufModelStore)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        _store = store;
        _ggufModelStore = ggufModelStore;
    }

    public async Task<ModelClassificationResult> SetOverrideAsync(string modelName, ModelKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var record = await _store.SetOverrideAsync(modelName, kind, cancellationToken);
        return await ToResultAsync(record, cancellationToken);
    }

    public async Task<ModelClassificationResult> ResetOverrideAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var cleared = await _store.SetOverrideAsync(modelName, overrideKind: null, cancellationToken);
        return await ToResultAsync(cleared, cancellationToken);
    }

    private async Task<ModelClassificationResult> ToResultAsync(ModelClassificationRecord record, CancellationToken cancellationToken)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken);
        var descriptor = installed.FirstOrDefault(model => string.Equals(model.ModelName, record.ModelName, StringComparison.OrdinalIgnoreCase));
        var resolved = LocalGgufModelKindClassifier.ResolveKind(record.ModelName, record, descriptor?.DecisionType);

        return new ModelClassificationResult
        {
            ModelName = record.ModelName,
            Kind = resolved.Kind,
            DetectedKind = resolved.DetectedKind,
            Capabilities = descriptor?.Capabilities ?? [],
            IsOverridden = resolved.IsOverridden
        };
    }
}
