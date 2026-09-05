namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class NodeSettingsAdministrationService(
    INodeSettingsStore store,
    INodeRuntimeSettings runtimeSettings,
    ICapabilityReporter capabilityReporter,
    DefaultModelSelectionPolicy defaultModelSelectionPolicy,
    IGgufModelStore ggufModelStore,
    IModelTrustResolver modelTrustResolver,
    ILocalModelProviderResolver localModelProviderResolver,
    ILogger<NodeSettingsAdministrationService> logger) : INodeSettingsAdministrationService
{
    private const string AutoEffortFastModelNotLocalMessage =
        "The fast model for automatic reasoning effort must be an installed node-local model.";

    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly DefaultModelSelectionPolicy _defaultModelSelectionPolicy = defaultModelSelectionPolicy ?? throw new ArgumentNullException(nameof(defaultModelSelectionPolicy));
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly ILocalModelProviderResolver _localModelProviderResolver = localModelProviderResolver ?? throw new ArgumentNullException(nameof(localModelProviderResolver));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly ILogger<NodeSettingsAdministrationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeRuntimeSettings _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    private readonly INodeSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<StoredNodeSettings> GetTrustedSettingsAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new StoredNodeSettings();

    public async Task<NodeSettingsAgenticView> GetAgenticViewAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);
        return ToAgenticView(settings);
    }

    public async Task<NodeSettingsAdministrationResult> SaveTrustedMergedAsync(StoredNodeSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);

        // LOCAL-ONLY members ride along from the stored record instead of from the caller. MachineKey is minted
        // node-side by IMachineKeyProvider and is deliberately absent from the wire DTO, so a caller that builds a
        // StoredNodeSettings out of a request has no value to supply and saving its record verbatim would erase the
        // key. That is silent data loss: the next start mints a fresh key, and every frozen inference profile — keyed
        // by machine key — is orphaned while still reading as frozen.
        var merged = settings with
        {
            MachineKey = current.MachineKey
        };

        return await ValidateAndSaveAsync(merged, current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeSettingsAdministrationResult> ApplyAgenticPatchAsync(NodeSettingsAgenticPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);
        var fieldErrors = NodeSettingsAgenticPatchValidation.Validate(patch);
        if (fieldErrors.Count > 0)
        {
            return NodeSettingsAdministrationResult.Rejected(current, fieldErrors);
        }

        if (patch.DefaultModelName is not null
            && await _defaultModelSelectionPolicy
                     .ValidateAsync(patch.DefaultModelName, LocalModelSelectionPolicy.ConfiguredModel, cancellationToken)
                     .ConfigureAwait(false) is { } selectionFailure)
        {
            return NodeSettingsAdministrationResult.Rejected(current,
            [
                new NodeSettingsValidationError(NodeSettingsField.DefaultModelName, selectionFailure.DisplayMessage)
            ]);
        }

        var merged = current with
        {
            DefaultModelName = TrimWhenProvided(patch.DefaultModelName, current.DefaultModelName),
            EnableTools = patch.EnableTools ?? current.EnableTools,
            ToolCapableModels = patch.ToolCapableModels ?? current.ToolCapableModels,
            HuggingFaceDefaultQuant = TrimWhenProvided(patch.HuggingFaceDefaultQuant, current.HuggingFaceDefaultQuant),
            LlamaMaxLoadedProcesses = patch.LlamaMaxLoadedProcesses ?? current.LlamaMaxLoadedProcesses,
            LlamaIdleTimeToLiveSeconds = patch.LlamaIdleTimeToLiveSeconds ?? current.LlamaIdleTimeToLiveSeconds,
            KeepModelWarmEnabled = patch.KeepModelWarmEnabled ?? current.KeepModelWarmEnabled,
            KeepModelWarmModelName = TrimWhenProvided(patch.KeepModelWarmModelName, current.KeepModelWarmModelName),
            KeepModelWarmIntervalSeconds = patch.KeepModelWarmIntervalSeconds ?? current.KeepModelWarmIntervalSeconds,
            MaxMessageRequestTimeoutSeconds = patch.MaxMessageRequestTimeoutSeconds ?? current.MaxMessageRequestTimeoutSeconds,
            ChatCacheReuse = patch.ChatCacheReuse ?? current.ChatCacheReuse,
            SpeculativeMode = TrimWhenProvided(patch.SpeculativeMode, current.SpeculativeMode),
            SpeculativeDraftModelName = TrimWhenProvided(patch.SpeculativeDraftModelName, current.SpeculativeDraftModelName),
            SpeculativeDraftMaxTokens = patch.SpeculativeDraftMaxTokens ?? current.SpeculativeDraftMaxTokens,
            SpeculativeDraftGpuLayers = patch.SpeculativeDraftGpuLayers ?? current.SpeculativeDraftGpuLayers,
            KvCacheType = TrimWhenProvided(patch.KvCacheType, current.KvCacheType),
            RerankerModelName = TrimWhenProvided(patch.RerankerModelName, current.RerankerModelName),
            AutoEffortFastModelName = TrimWhenProvided(patch.AutoEffortFastModelName, current.AutoEffortFastModelName)
        };

        var result = await ValidateAndSaveAsync(merged, current, cancellationToken).ConfigureAwait(false);
        if (result.Updated && patch.DefaultModelName is not null)
        {
            await _defaultModelSelectionPolicy
                  .InvalidateCacheForTransitionAsync(current.DefaultModelName, merged.DefaultModelName, cancellationToken)
                  .ConfigureAwait(false);
        }

        return result;
    }

    private async Task<NodeSettingsAdministrationResult> ValidateAndSaveAsync(StoredNodeSettings settings,
        StoredNodeSettings current,
        CancellationToken cancellationToken)
    {
        // Enforcement point 1 of the node-locality gate, on BOTH save paths (the endpoint's merged save and the MCP
        // patch) rather than only the patch: the runner's dispatcher may move an `auto` turn onto this model, and the
        // turn's data was admitted upstream against a node-local one. A cloud id, an `ext:` id or an Ollama name would
        // carry that data somewhere no egress gate authorised, so it is refused before it can ever be stored.
        //
        // Only on a CHANGE to the value, though. Both save paths validate the merged result, so re-validating an
        // unchanged stored value would reject every save of every other setting once the configured fast model is
        // uninstalled — with a message naming a field the operator never touched. The dispatcher re-checks the same
        // pair per turn (enforcement point 2), so an already-stored value that stops being node-local is refused where
        // it would actually be used rather than blocking the settings page.
        if (!string.Equals(settings.AutoEffortFastModelName, current.AutoEffortFastModelName, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(settings.AutoEffortFastModelName)
            && !await IsInstalledNodeLocalModelAsync(settings.AutoEffortFastModelName, cancellationToken).ConfigureAwait(false))
        {
            return NodeSettingsAdministrationResult.Rejected(settings,
            [
                new NodeSettingsValidationError(NodeSettingsField.AutoEffortFastModelName, AutoEffortFastModelNotLocalMessage)
            ]);
        }

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(settings, _runtimeSettings, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return NodeSettingsAdministrationResult.Rejected(settings, errors);
        }

        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return NodeSettingsAdministrationResult.Saved(settings);
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after node settings were saved.");
        }
    }

    // Enforcement point 1's predicate. Shared verbatim with enforcement point 2 (the dispatcher's per-turn re-check)
    // so a value this save accepts is exactly a value that turn admits, and the registry membership test is what stops
    // an arbitrary string — a cloud model id included — from passing two resolvers that both default the unknown to
    // "node-local llama.cpp".
    private Task<bool> IsInstalledNodeLocalModelAsync(string modelName, CancellationToken cancellationToken) =>
        NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(modelName,
            _ggufModelStore,
            _modelTrustResolver,
            _localModelProviderResolver,
            cancellationToken);

    private static string? TrimWhenProvided(string? value, string? current) =>
        value is null ? current : value.Trim();

    private static NodeSettingsAgenticView ToAgenticView(StoredNodeSettings settings) =>
        new(settings.DefaultModelName,
            settings.EnableTools,
            settings.ToolCapableModels,
            settings.HuggingFaceDefaultQuant,
            settings.LlamaMaxLoadedProcesses,
            settings.LlamaIdleTimeToLiveSeconds,
            settings.KeepModelWarmEnabled,
            settings.KeepModelWarmModelName,
            settings.KeepModelWarmIntervalSeconds,
            settings.MaxMessageRequestTimeoutSeconds,
            settings.ChatCacheReuse,
            settings.SpeculativeMode,
            settings.SpeculativeDraftModelName,
            settings.SpeculativeDraftMaxTokens,
            settings.SpeculativeDraftGpuLayers,
            settings.KvCacheType,
            settings.RerankerModelName,
            settings.AutoEffortFastModelName);
}
