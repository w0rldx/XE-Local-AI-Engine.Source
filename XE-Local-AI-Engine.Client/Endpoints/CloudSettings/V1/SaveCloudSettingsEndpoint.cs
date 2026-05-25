namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class SaveCloudSettingsEndpoint(
    ICloudCredentialStore cloudCredentialStore,
    ICapabilityReporter capabilityReporter,
    ILogger<SaveCloudSettingsEndpoint> logger) : Endpoint<SaveCloudSettingsRequest, CloudSettingsResponse>
{
    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
    private readonly ILogger<SaveCloudSettingsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Put(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SaveCloudSettingsRequest req, CancellationToken ct)
    {
        var credentials = req.ToStoredCredentials();
        await _cloudCredentialStore.SaveAsync(credentials, ct).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(credentials.ToResponse(), ct).ConfigureAwait(false);
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after cloud settings were saved.");
        }
    }
}
