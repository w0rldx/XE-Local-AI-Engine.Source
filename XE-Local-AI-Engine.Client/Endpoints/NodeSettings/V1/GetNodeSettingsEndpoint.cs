namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class GetNodeSettingsEndpoint : EndpointWithoutRequest<NodeSettingsResponse>
{
    private readonly INodeSettingsAdministrationService _administrationService;

    public GetNodeSettingsEndpoint(INodeSettingsAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.NodeSettings.Settings);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _administrationService.GetTrustedSettingsAsync(ct);
        await Send.OkAsync(settings.ToResponse(), ct);
    }
}
