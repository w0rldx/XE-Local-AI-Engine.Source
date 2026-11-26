namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class ListModelLabPlaygroundRunsEndpoint : EndpointWithoutRequest<ModelLabPlaygroundRunListResponse>
{
    private readonly ModelLabPlaygroundService _playground;

    public ListModelLabPlaygroundRunsEndpoint(ModelLabPlaygroundService playground)
    {
        ArgumentNullException.ThrowIfNull(playground);
        _playground = playground;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.PlaygroundRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ModelLabPlaygroundRunListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var runs = await _playground.ListAsync(ct);
        var supported = _playground.IsPlatformSupported;
        await Send.OkAsync(new ModelLabPlaygroundRunListResponse
        {
            PlatformSupported = supported,
            UnsupportedMessage = supported ? null : ModelLabPlaygroundService.UnsupportedPlatformMessage,
            MaxRetainedRuns = ModelLabPlaygroundSpec.MaxRetainedRuns,
            Runs = [.. runs.Select(ModelLabPlaygroundMapper.ToResponse)]
        }, ct);
    }
}
