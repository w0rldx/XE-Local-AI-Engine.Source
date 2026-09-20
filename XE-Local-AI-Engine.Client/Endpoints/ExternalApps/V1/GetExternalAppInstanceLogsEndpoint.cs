namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>A bounded tail of one service's container log, read live and never persisted.</summary>
/// <remarks>
///     The text crosses UNMASKED by design: it is the application's own output, and an application printing its own
///     secrets is something its operator has to be able to see.
/// </remarks>
public sealed class GetExternalAppInstanceLogsEndpoint : Endpoint<ExternalAppInstanceLogsRequest, ExternalAppInstanceLogsResponse>
{
    private readonly IExternalAppService _apps;

    public GetExternalAppInstanceLogsEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstanceLogs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound)
                                             .ProducesProblemDetails(StatusCodes.Status503ServiceUnavailable));
    }

    public override async Task HandleAsync(ExternalAppInstanceLogsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The read runs FIRST, so an unknown instance or an unknown service is a 404 from the service rather than
        // something this endpoint has to decide for itself.
        var snapshot = await _apps.ReadLogsAsync(req.InstanceId, req.Service, req.Tail, ct);

        var service = req.Service ?? await ResolveDefaultServiceAsync(req.InstanceId, ct);

        await Send.OkAsync(new ExternalAppInstanceLogsResponse { Service = service, Text = snapshot.Text, LineCount = snapshot.LineCount, Truncated = snapshot.Truncated }, ct);
    }

    /// <summary>
    ///     Which service an omitted <c>?service=</c> read: the first one publishing a port, else the first declared.
    /// </summary>
    /// <remarks>
    ///     The snapshot the runtime returns carries no service name, so the response would otherwise echo a null the
    ///     caller cannot page or refresh with. It costs one extra read, and only when the caller named no service.
    /// </remarks>
    private async Task<string> ResolveDefaultServiceAsync(Guid instanceId, CancellationToken ct)
    {
        var detail = await _apps.GetAsync(instanceId, ct);
        var services = detail.Manifest.Services;

        ApplicationService? published = null;
        foreach (var candidate in services)
        {
            if (candidate.Ports.Count > 0)
            {
                published = candidate;
                break;
            }
        }

        return (published ?? services[0]).Name;
    }
}
