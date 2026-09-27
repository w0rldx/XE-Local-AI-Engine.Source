namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class GetGgufImportCapabilityEndpoint : EndpointWithoutRequest<GgufImportCapabilityResponse>
{
    private readonly NodeLaunchContext _launchContext;

    public GetGgufImportCapabilityEndpoint(NodeLaunchContext launchContext)
    {
        ArgumentNullException.ThrowIfNull(launchContext);
        _launchContext = launchContext;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.ImportCapability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new GgufImportCapabilityResponse
        {
            Available = _launchContext.IsLocalMode
        }, ct);
    }
}
