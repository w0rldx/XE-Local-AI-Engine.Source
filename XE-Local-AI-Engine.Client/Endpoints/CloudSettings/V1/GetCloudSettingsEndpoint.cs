namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     FastEndpoints handler for the get cloud settings local API operation.
/// </summary>
public sealed class GetCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore) : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var credentials = await _cloudCredentialStore.LoadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(credentials.ToResponse(), ct).ConfigureAwait(false);
    }
}
