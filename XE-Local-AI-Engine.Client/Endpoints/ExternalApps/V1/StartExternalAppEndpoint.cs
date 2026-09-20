namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Starts the instance, rebuilding its containers first when they are missing, stale or unverifiable.
/// </summary>
/// <remarks>
///     202 with the ADMITTED row, then the hub — see docs/wiki/09-api-and-hubs.md ("Design notes on the newer
///     endpoint families").
/// </remarks>
public sealed class StartExternalAppEndpoint : Endpoint<ExternalAppInstanceCommandRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps;

    public StartExternalAppEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.InstanceStart);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<ExternalAppInstanceSummaryView>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ExternalAppInstanceCommandRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator's NotNull rule ran first, so the value is present here and only here.
        var summary = await _apps.StartAsync(req.InstanceId, req.ExpectedVersion!.Value, ct);
        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary)));
    }
}
