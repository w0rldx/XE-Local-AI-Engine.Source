namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     One page of the instance's append-only history, ascending and strictly after the caller's watermark. It reads
///     the same <c>ListEventsAsync</c> the hub replays from, so the History tab and a fresh subscription cannot show
///     two different pasts.
/// </summary>
public sealed class ListExternalAppInstanceEventsEndpoint(IExternalAppService apps)
    : Endpoint<ExternalAppInstanceEventFeedRequest, ListExternalAppInstanceEventsResponse>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstanceEvents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppInstanceEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // One row past the page: `hasMore` is then OBSERVED rather than inferred from a full page, which would report
        // "more" for the last page whenever the history happens to be a multiple of the limit.
        var page = await _apps.ListEventsAsync(req.InstanceId, req.AfterSequence, req.Limit + 1, ct);

        var count = Math.Min(page.Count, req.Limit);
        var items = new List<ExternalAppInstanceEventView>(count);
        for (var index = 0; index < count; index++)
        {
            items.Add(ExternalAppMapper.ToEventView(page[index]));
        }

        // The watermark the caller resumes from: the last sequence it was actually shown, and its own watermark
        // unchanged on an empty page, so "load more" against an idle instance cannot skip a row minted meanwhile.
        var highest = count == 0 ? req.AfterSequence : items[count - 1].Sequence;

        await Send.OkAsync(new ListExternalAppInstanceEventsResponse(items, highest, page.Count > req.Limit), ct);
    }
}
