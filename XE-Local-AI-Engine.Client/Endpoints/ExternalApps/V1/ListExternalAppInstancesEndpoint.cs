namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>Every installed instance in full.</summary>
/// <remarks>
///     The card shows an Open target, a published port and the runtime provider, all of which live on the detail, and
///     the store reads whole rows regardless — so a summary list would buy nothing and cost the page one GET per card
///     before it could render.
/// </remarks>
public sealed class ListExternalAppInstancesEndpoint : EndpointWithoutRequest<ListExternalAppInstancesResponse>
{
    private readonly IExternalAppService _apps;

    public ListExternalAppInstancesEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.Instances);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var instances = await _apps.ListDetailsAsync(ct);

        var items = new List<ExternalAppInstanceView>(instances.Count);
        foreach (var instance in instances)
        {
            items.Add(ExternalAppMapper.ToView(instance));
        }

        await Send.OkAsync(new ListExternalAppInstancesResponse { Items = items }, ct);
    }
}
