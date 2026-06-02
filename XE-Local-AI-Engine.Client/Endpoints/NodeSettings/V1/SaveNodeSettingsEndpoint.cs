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
