namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Rewrites a STOPPED instance's variables. 200 and not 202: a created container's environment is immutable, so
///     the write is complete when it returns and the values take effect on the next start.
/// </summary>
/// <remarks>
///     A FULL replacement of the declared set: the mask sentinel keeps a stored secret, a real value replaces it, an
///     empty string clears it. Reconfiguring a running instance is a 409 <c>ExternalAppInvalidTransition</c>.
/// </remarks>
public sealed class UpdateExternalAppVariablesEndpoint : Endpoint<UpdateExternalAppVariablesRequest, ExternalAppInstanceView>
{
    private readonly IExternalAppService _apps;

    public UpdateExternalAppVariablesEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.ExternalApps.InstanceVariables);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound).ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateExternalAppVariablesRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator's NotNull rule ran first, so the value is present here and only here.
        var detail = await _apps.ConfigureAsync(req.InstanceId, req.ExpectedVersion!.Value, req.Variables, ct);
        await Send.OkAsync(ExternalAppMapper.ToView(detail), ct);
    }
}
