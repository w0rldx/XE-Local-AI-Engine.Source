namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class ClearCloudSettingsEndpoint(
    ICloudCredentialStore cloudCredentialStore,
    ICapabilityReporter capabilityReporter,
    ILogger<ClearCloudSettingsEndpoint> logger) : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
    private readonly ILogger<ClearCloudSettingsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Delete(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _cloudCredentialStore.ClearAsync(ct).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(CloudSettingsResponse.Empty, ct).ConfigureAwait(false);
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after cloud settings were cleared.");
        }
    }
}
