namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class ClearCloudSettingsEndpoint : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly ICloudCredentialStore _cloudCredentialStore;

    public ClearCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        _cloudCredentialStore = cloudCredentialStore;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _cloudCredentialStore.ClearAsync(ct);
        await Send.OkAsync(CloudSettingsResponse.Empty, ct);
    }
}
