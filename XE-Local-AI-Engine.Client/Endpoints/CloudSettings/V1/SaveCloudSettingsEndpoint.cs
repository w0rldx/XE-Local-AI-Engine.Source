namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class SaveCloudSettingsEndpoint : Endpoint<SaveCloudSettingsRequest, CloudSettingsResponse>
{
    private readonly ICloudCredentialStore _cloudCredentialStore;

    public SaveCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        _cloudCredentialStore = cloudCredentialStore;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SaveCloudSettingsRequest req, CancellationToken ct)
    {
        // Load prior state so a secret header re-sent with a blank value keeps its stored value. The mapper stays pure — the merge is the only impure step and it runs here —
        // and the load happens before validation so the validator can tell a fresh or renamed blank secret header (rejected, 400) from one that resolves via the stored merge.
        var existing = await _cloudCredentialStore.LoadConfigAsync(ct);
        var existingHeaders = existing?.AzureFoundry?.Headers ?? [];

        // Reserved-name, character-set, capacity, and host-suffix validation. Error messages carry only the offending
        // header name, never its value.
        var headerErrors = CloudSettingsPolicy.ValidateHeadersAndSuffixes(req.Headers.ToPolicyHeaders(),
            req.AdditionalAllowedHostSuffixes,
            existingHeaders);
        if (headerErrors.Count > 0)
        {
            foreach (var headerError in headerErrors)
            {
                AddError(headerError);
            }

            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var mergedHeaders = CloudSettingsHeaderMerge.Merge(existingHeaders, req.Headers);
        var mergedEntraClientSecret = CloudSettingsEntraSecretMerge.Merge(existing?.AzureFoundry, req.EntraClientSecret);

        // AuthorizationCode redeems the code with the client secret (confidential client), so it requires one, typed on this request or previously stored. Checked after the
        // merge resolves whether a secret is available, so a secret-less connection gets a clean 400 instead of letting CloudCredentialStore.ValidateConfig throw a 500 on save.
        if (CloudSettingsEndpointDtoMapper.RequestsAuthorizationCode(req) && string.IsNullOrWhiteSpace(mergedEntraClientSecret))
        {
            AddError("EntraSignInMethod is 'AuthorizationCode', which requires a client secret (typed on this request or previously stored).");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var config = req.ToStoredConfig(mergedHeaders, mergedEntraClientSecret);
        await _cloudCredentialStore.SaveConfigAsync(config, ct);
        await Send.OkAsync(config.ToResponse(), ct);
    }
}
