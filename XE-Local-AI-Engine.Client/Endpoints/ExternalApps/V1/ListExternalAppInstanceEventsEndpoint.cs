namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     One page of the instance's append-only history, ascending and strictly after the caller's watermark. It reads
///     the same <c>ListEventsAsync</c> the hub replays from, so the History tab and a fresh subscription cannot show
///     two different pasts.
/// </summary>
public sealed class ListExternalAppInstanceEventsEndpoint : Endpoint<ExternalAppInstanceEventFeedRequest, ListExternalAppInstanceEventsResponse>
{
    private readonly IExternalAppService _apps;

    public ListExternalAppInstanceEventsEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstanceEvents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppInstanceEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var (page, hasMore) = await ReplayWindow.ReadAsync(req.Limit, limit => _apps.ListEventsAsync(req.InstanceId, req.AfterSequence, limit, ct));
        var items = page.Select(ExternalAppMapper.ToEventView).ToList();

        // The watermark the caller resumes from: the last sequence it was actually shown, and its own watermark
        // unchanged on an empty page, so "load more" against an idle instance cannot skip a row minted meanwhile.
        var highest = items.Count == 0 ? req.AfterSequence : items[^1].Sequence;

        await Send.OkAsync(new ListExternalAppInstanceEventsResponse
        {
            Items = items,
            HighestSequence = highest,
            HasMore = hasMore
        }, ct);
    }
}
