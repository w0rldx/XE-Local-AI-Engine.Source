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

        // Cross-field guards run on the MERGED result in NodeSettingsPolicy — the boundary validator only sees the
        // request, not the current stored state, and some rules need the EFFECTIVE runtime value (stored > appsettings
        // seed > default) for a knob the request omitted. The policy stops at the first violation, matching the
        // one-error-at-a-time response this endpoint has always sent.
        var policyErrors = await NodeSettingsPolicy.ValidateMergedAsync(settings, _nodeRuntimeSettings, ct).ConfigureAwait(false);
        if (policyErrors.Count > 0)
        {
            foreach (var policyError in policyErrors)
            {
                AddPolicyError(policyError);
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await SaveAsync(settings, ct).ConfigureAwait(false);
    }

    // Maps a policy violation back onto the request property it belongs to, so the 400 body keeps naming the same
    // field it always has.
    private void AddPolicyError(NodeSettingsValidationError error)
    {
        switch (error.Field)
        {
            case NodeSettingsField.SpeculativeDraftModelName:
                AddError(r => r.SpeculativeDraftModelName, error.Message);
                break;
            case NodeSettingsField.KeepModelWarmModelName:
                AddError(r => r.KeepModelWarmModelName, error.Message);
                break;
            case NodeSettingsField.LlamaMaxLoadedProcesses:
                AddError(r => r.LlamaMaxLoadedProcesses, error.Message);
                break;
            case NodeSettingsField.KeepModelWarmIntervalSeconds:
                AddError(r => r.KeepModelWarmIntervalSeconds, error.Message);
                break;
            default:
                AddError(error.Message);
                break;
        }
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
