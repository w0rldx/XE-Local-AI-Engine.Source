namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Models;

internal sealed class NodeSettingsAdministrationService(
    INodeSettingsStore store,
    INodeRuntimeSettings runtimeSettings,
    ICapabilityReporter capabilityReporter,
    DefaultModelSelectionPolicy defaultModelSelectionPolicy,
    ILogger<NodeSettingsAdministrationService> logger) : INodeSettingsAdministrationService
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly DefaultModelSelectionPolicy _defaultModelSelectionPolicy = defaultModelSelectionPolicy ?? throw new ArgumentNullException(nameof(defaultModelSelectionPolicy));
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
        return await ValidateAndSaveAsync(settings, cancellationToken).ConfigureAwait(false);
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
            RerankerModelName = TrimWhenProvided(patch.RerankerModelName, current.RerankerModelName)
        };

        var result = await ValidateAndSaveAsync(merged, cancellationToken).ConfigureAwait(false);
        if (result.Updated && patch.DefaultModelName is not null)
        {
            await _defaultModelSelectionPolicy
                  .InvalidateCacheForTransitionAsync(current.DefaultModelName, merged.DefaultModelName, cancellationToken)
                  .ConfigureAwait(false);
        }

        return result;
    }

    private async Task<NodeSettingsAdministrationResult> ValidateAndSaveAsync(StoredNodeSettings settings,
        CancellationToken cancellationToken)
    {
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

    private static string? TrimWhenProvided(string? value, string? current) => value is null ? current : value.Trim();

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
            settings.RerankerModelName);
}
