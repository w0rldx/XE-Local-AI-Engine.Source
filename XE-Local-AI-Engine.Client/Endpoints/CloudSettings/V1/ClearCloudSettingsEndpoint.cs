namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class ClearCloudSettingsEndpoint : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter;
    private readonly ICloudCredentialStore _cloudCredentialStore;
    private readonly ILogger<ClearCloudSettingsEndpoint> _logger;

    public ClearCloudSettingsEndpoint(
        ICloudCredentialStore cloudCredentialStore,
        ICapabilityReporter capabilityReporter,
        ILogger<ClearCloudSettingsEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(capabilityReporter);
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        ArgumentNullException.ThrowIfNull(logger);
        _capabilityReporter = capabilityReporter;
        _cloudCredentialStore = cloudCredentialStore;
        _logger = logger;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _cloudCredentialStore.ClearAsync(ct);
        await TryReportCapabilitiesAsync(ct);
        await Send.OkAsync(CloudSettingsResponse.Empty, ct);
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after cloud settings were cleared.");
        }
    }
}
