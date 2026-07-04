namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class SaveNodeSettingsEndpoint(
    INodeSettingsStore nodeSettingsStore,
    ICapabilityReporter capabilityReporter,
    ILogger<SaveNodeSettingsEndpoint> logger) : Endpoint<SaveNodeSettingsRequest, NodeSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly ILogger<SaveNodeSettingsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
