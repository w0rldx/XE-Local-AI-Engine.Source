namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed class ClearCloudSettingsEndpoint : EndpointWithoutRequest<CloudSettingsResponse>
{
    private readonly IActiveCloudChatClientFactory _cloudChatClientFactory;
    private readonly ICloudCredentialStore _cloudCredentialStore;

    public ClearCloudSettingsEndpoint(ICloudCredentialStore cloudCredentialStore, IActiveCloudChatClientFactory cloudChatClientFactory)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        ArgumentNullException.ThrowIfNull(cloudChatClientFactory);
        _cloudCredentialStore = cloudCredentialStore;
        _cloudChatClientFactory = cloudChatClientFactory;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.CloudSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _cloudCredentialStore.ClearAsync(ct);
        // Routing reads a cached snapshot; drop it so the cleared deployments stop routing on the next send.
        _cloudChatClientFactory.InvalidateSelectionCache();
        await Send.OkAsync(CloudSettingsResponse.Empty, ct);
    }
}
