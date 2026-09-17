namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     One instance in full, carrying the SANITISED INSTALLED manifest rather than the catalog's current one: an
///     instance installed at v2 renders what it is running, and an instance whose application has left the catalog
///     must still render at all.
/// </summary>
public sealed class GetExternalAppInstanceEndpoint(IExternalAppService apps) : Endpoint<ExternalAppInstanceRequest, ExternalAppInstanceView>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstanceById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppInstanceRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _apps.GetAsync(req.InstanceId, ct);
        await Send.OkAsync(ExternalAppMapper.ToView(detail), ct);
    }
}
