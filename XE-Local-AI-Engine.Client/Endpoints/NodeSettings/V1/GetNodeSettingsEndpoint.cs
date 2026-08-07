namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class GetNodeSettingsEndpoint(INodeSettingsStore nodeSettingsStore) : EndpointWithoutRequest<NodeSettingsResponse>
{
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.NodeSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(settings.ToResponse(), ct).ConfigureAwait(false);
    }
}
