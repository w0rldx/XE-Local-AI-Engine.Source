namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class SaveNodeSettingsEndpoint(
    INodeSettingsStore nodeSettingsStore,
    INodeRuntimeSettings nodeRuntimeSettings,
    ICapabilityReporter capabilityReporter,
    ILogger<SaveNodeSettingsEndpoint> logger) : Endpoint<SaveNodeSettingsRequest, NodeSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly ILogger<SaveNodeSettingsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    public override void Configure()
    {
        Put(LocalApiRoutes.NodeSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SaveNodeSettingsRequest req, CancellationToken ct)
    {
        var currentSettings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false) ?? new StoredNodeSettings();
        var settings = req.ToStoredSettings(currentSettings);

        // Cross-field guard on the MERGED result (the boundary validator only sees the request, not the current stored
        // state): a draft-* speculative mode with no draft model must never persist — it would pass every field-level
        // check and then fail chat-server start on the next spawn. This also covers the partial update that clears the
        // draft model while leaving a previously-stored draft-* mode in place.
        if (StoredNodeSettings.SpeculativeModeRequiresDraftModel(settings.SpeculativeMode)
            && string.IsNullOrWhiteSpace(settings.SpeculativeDraftModelName))
        {
            AddError(r => r.SpeculativeDraftModelName, "Speculative decoding is set to a draft model mode, but no draft model was selected.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // Like the speculative-mode guard above, validate the MERGED result so a partial request may enable the feature
        // while keeping an already-stored model, but can never persist an enabled state with no selected model.
        if (settings.KeepModelWarmEnabled is true
            && string.IsNullOrWhiteSpace(settings.KeepModelWarmModelName))
        {
            AddError(r => r.KeepModelWarmModelName, "Keep model warm is enabled, but no model was selected.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (settings.KeepModelWarmEnabled is not true)
        {
            await SaveAsync(settings, ct).ConfigureAwait(false);
            return;
        }

        var effectiveMaxLoadedProcesses = settings.LlamaMaxLoadedProcesses
                                          ?? await _nodeRuntimeSettings.GetLlamaMaxLoadedProcessesAsync(ct).ConfigureAwait(false);
        if (effectiveMaxLoadedProcesses < 2)
        {
            AddError(r => r.LlamaMaxLoadedProcesses,
                "Keep model warm requires at least two loaded-process slots so another local model can still be admitted.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var effectiveKeepWarmInterval = settings.KeepModelWarmIntervalSeconds is { } intervalSeconds
            ? TimeSpan.FromSeconds(intervalSeconds)
            : await _nodeRuntimeSettings.GetKeepModelWarmIntervalAsync(ct).ConfigureAwait(false);
        var effectiveIdleTimeToLive = settings.LlamaIdleTimeToLiveSeconds is { } idleTimeToLiveSeconds
            ? TimeSpan.FromSeconds(idleTimeToLiveSeconds)
            : await _nodeRuntimeSettings.GetLlamaIdleTimeToLiveAsync(ct).ConfigureAwait(false);
        if (effectiveKeepWarmInterval >= effectiveIdleTimeToLive)
        {
            AddError(r => r.KeepModelWarmIntervalSeconds,
                "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await SaveAsync(settings, ct).ConfigureAwait(false);
    }

    private async Task SaveAsync(StoredNodeSettings settings, CancellationToken ct)
    {
        await _nodeSettingsStore.SaveAsync(settings, ct).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(settings.ToResponse(), ct).ConfigureAwait(false);
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
}
