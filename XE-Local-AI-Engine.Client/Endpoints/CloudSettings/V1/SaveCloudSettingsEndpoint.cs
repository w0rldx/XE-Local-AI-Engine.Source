namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1.Mappers;
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
        // Load prior state so a secret header re-sent with a blank value keeps its stored value (Locked #12). The mapper
        // stays pure — the merge is the only impure step and it runs here. Loaded before validation so the validator can
        // tell a fresh/renamed blank secret header (rejected, 400) apart from one that resolves via the stored merge.
        var existing = await _cloudCredentialStore.LoadConfigAsync(ct).ConfigureAwait(false);
        var existingHeaders = existing?.AzureFoundry?.Headers ?? [];

        // Reserved / charset / caps / host-suffix validation. Error messages carry the offending header NAME only —
        // never a value (Locked #6–#9, #14).
        var headerErrors = CloudSettingsPolicy.ValidateHeadersAndSuffixes(req.Headers.ToPolicyHeaders(),
            req.AdditionalAllowedHostSuffixes,
            existingHeaders);
        if (headerErrors.Count > 0)
        {
            foreach (var headerError in headerErrors)
            {
                AddError(headerError);
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var mergedHeaders = CloudSettingsHeaderMerge.Merge(existingHeaders, req.Headers);
        var mergedEntraClientSecret = CloudSettingsEntraSecretMerge.Merge(existing?.AzureFoundry, req.EntraClientSecret);

        // AuthorizationCode redeems the code with the client secret (confidential client), so it requires one —
        // typed on this request or previously stored. Checked here, after the merge resolves whether a secret is
        // actually available, so a fresh/renamed secret-less AuthorizationCode connection gets a clean 400 instead
        // of letting CloudCredentialStore.ValidateConfig throw on save (500) — mirrors the secret-header pattern in
        // CloudSettingsPolicy.ValidateHeadersAndSuffixes.
        if (CloudSettingsEndpointDtoMapper.RequestsAuthorizationCode(req) && string.IsNullOrWhiteSpace(mergedEntraClientSecret))
        {
            AddError("EntraSignInMethod is 'AuthorizationCode', which requires a client secret (typed on this request or previously stored).");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var config = req.ToStoredConfig(mergedHeaders, mergedEntraClientSecret);
        await _cloudCredentialStore.SaveConfigAsync(config, ct).ConfigureAwait(false);
        await TryReportCapabilitiesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(config.ToResponse(), ct).ConfigureAwait(false);
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
