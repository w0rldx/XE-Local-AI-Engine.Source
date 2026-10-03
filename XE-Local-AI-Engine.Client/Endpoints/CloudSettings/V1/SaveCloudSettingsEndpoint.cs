namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed class SaveCloudSettingsEndpoint : Endpoint<SaveCloudSettingsRequest, CloudSettingsResponse>
{
    private readonly IActiveCloudChatClientFactory _cloudChatClientFactory;
    private readonly ICloudCredentialStore _cloudCredentialStore;
    private readonly IGgufModelStore _ggufModelStore;

    public SaveCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore, IGgufModelStore ggufModelStore, IActiveCloudChatClientFactory cloudChatClientFactory)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(cloudChatClientFactory);
        _cloudCredentialStore = cloudCredentialStore;
        _ggufModelStore = ggufModelStore;
        _cloudChatClientFactory = cloudChatClientFactory;
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

        // A deployment name wins routing over any local model of the same name, so a name that shadows an external id, a
        // Codex id or an installed GGUF (the default runtime) is refused here rather than silently hijacking that model.
        var deploymentNames = req.Models.Select(static model => model.DeploymentName?.Trim()).ToList();
        var nameErrors = new List<string>(CloudSettingsPolicy.ValidateDeploymentNames(deploymentNames));
        // Case-insensitive, because Azure routing matches deployment names ignoring case: an ordinal check would let
        // `Qwen3-8B` through and then capture every send to the installed `qwen3-8b`.
        var installedNames = (await _ggufModelStore.ListInstalledModelsAsync(ct)).Select(static model => model.ModelName)
                                                                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        nameErrors.AddRange(deploymentNames.Where(name => !string.IsNullOrEmpty(name) && installedNames.Contains(name))
                                           .Select(static name => $"Deployment name '{name}' is an installed local model and would shadow it."));

        if (headerErrors.Count > 0 || nameErrors.Count > 0)
        {
            foreach (var error in nameErrors.Concat(headerErrors))
            {
                AddError(error);
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
        // Routing reads a cached snapshot; drop it so renamed or removed deployments take effect on the next send.
        _cloudChatClientFactory.InvalidateSelectionCache();
        await Send.OkAsync(config.ToResponse(), ct);
    }
}
