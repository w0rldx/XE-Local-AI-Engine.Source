namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class GetCloudSettingsEndpoint : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly ICloudCredentialStore _cloudCredentialStore;

    public GetCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        _cloudCredentialStore = cloudCredentialStore;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var config = await _cloudCredentialStore.LoadConfigAsync(ct);
        await Send.OkAsync(config.ToResponse(), ct);
    }
}
