namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

public static class ExternalAppHubEvents
{
    public const string Changed = "externalAppChanged";

    public const string PullProgress = "externalAppPullProgress";
}

/// <summary>
///     A content-free ping: what happened, where the instance now stands, and the sequence it was minted at. The
///     subscriber re-reads the feed from its own watermark, so a dropped push degrades to a late read rather than to a
///     wrong render — and nothing an instance's variables could reach ever rides on the hub.
/// </summary>
public sealed record ExternalAppChanged(Guid InstanceId, long Sequence, string Kind, string Status);

/// <summary>
///     Image-pull progress for one service. Hub-only: high-frequency and worthless after the fact, so it allocates no
///     sequence and appends no event row.
/// </summary>
public sealed record ExternalAppPullProgress(Guid InstanceId, string Service, int LayerCount, int CompletedLayers, long Bytes);

public sealed record ExternalAppSubscriptionSnapshot(
    Guid InstanceId,
    string Status,
    string DesiredState,
    string? FailureCategory,
    long LastSequence,
    IReadOnlyList<ExternalAppInstanceEventView> Events,
    bool ReplayTruncated);

/// <summary>
///     Operator-only live notifications for one external application instance.
///     <para>
///         There is no in-memory buffer: instance events are persisted append-only with a monotonic sequence and
///         <see cref="IExternalAppService.ListEventsAsync" /> IS the replay authority — literally the same member the
///         events endpoint pages, so a subscription and the History tab cannot show two different pasts. Nor does a
///         disconnect cancel anything: an install outlives the browser tab that started it.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class ExternalAppHub(IExternalAppService apps, IOptions<ExternalAppsOptions> options) : Hub
{
    /// <summary>
    ///     How many persisted events one subscribe hands back. Past this the snapshot says so and the client pages the
    ///     event feed by <c>afterSequence</c> — one extra round trip on a long-lived instance, against an unbounded
    ///     first frame for every subscriber.
    /// </summary>
    private const int ReplayCap = 200;

    /// <summary>
    ///     One wording for "no such instance", because BOTH reads below can be the one that notices: the instance can
    ///     be deleted between them, and the replay read carries its own existence check.
    /// </summary>
    private const string InstanceNotFoundMessage = "External app instance was not found.";

    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly ExternalAppsOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public async Task<ExternalAppSubscriptionSnapshot> Subscribe(Guid instanceId, long afterSequence)
    {
        if (!_options.Enabled)
        {
            throw new HubException("External apps are disabled on this node.");
        }

        if (instanceId == Guid.Empty)
        {
            throw new HubException("External app instance id is required.");
        }

        if (afterSequence < 0)
        {
            throw new HubException("External app replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        ExternalAppInstanceDetail detail;
        try
        {
            detail = await _apps.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalAppNotFoundException)
        {
            throw new HubException(InstanceNotFoundMessage);
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between the read
        // and the join reaches nobody. The overlap this creates is harmless — every push is an idempotent notification
        // keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), cancellationToken).ConfigureAwait(false);

        try
        {
            // One over the cap, so "there is more" is observed rather than inferred from a full page.
            var events = await _apps.ListEventsAsync(instanceId, afterSequence, ReplayCap + 1, cancellationToken).ConfigureAwait(false);

            return new ExternalAppSubscriptionSnapshot(instanceId,
                detail.Summary.Status.ToString(),
                detail.Summary.DesiredState.ToString(),
                detail.Summary.FailureCategory?.ToString(),
                detail.LastSequence,
                [.. events.Take(ReplayCap).Select(ExternalAppMapper.ToEventView)],
                events.Count > ReplayCap);
        }
        catch (ExternalAppNotFoundException)
        {
            // The instance was deleted between the two reads. The caller is told what it would have been told had the
            // first read noticed — a generic hub failure would make a routine race look like a node fault.
            await LeaveAfterFailedSubscribeAsync(instanceId).ConfigureAwait(false);
            throw new HubException(InstanceNotFoundMessage);
        }
        catch
        {
            await LeaveAfterFailedSubscribeAsync(instanceId).ConfigureAwait(false);
            throw;
        }
    }

    public Task Unsubscribe(Guid instanceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), Context.ConnectionAborted);

    /// <summary>
    ///     A subscribe that threw hands its caller no watermark and no handle to unsubscribe with, so a membership left
    ///     behind would push this instance's changes at a connection that never received its replay. Rolled back with
    ///     <see cref="CancellationToken.None" />: the failure being an aborted connection is exactly the case where the
    ///     rollback must still run rather than inherit the cancellation and mask the original exception.
    /// </summary>
    private Task LeaveAfterFailedSubscribeAsync(Guid instanceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), CancellationToken.None);
}

internal static class ExternalAppHubGroups
{
    public static string Instance(Guid instanceId) =>
        string.Concat("external-app-", instanceId.ToString("N"));
}
