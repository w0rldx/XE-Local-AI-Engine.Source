namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class GetGgufImportCapabilityEndpoint : EndpointWithoutRequest<GgufImportCapabilityResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.ImportCapability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var available = DesktopLaunch.IsDesktopMode(Environment.GetCommandLineArgs(), VelopackInstall.IsManaged());
        await Send.OkAsync(new GgufImportCapabilityResponse { Available = available }, ct).ConfigureAwait(false);
    }
}
