namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Operator-only live notifications for one external application instance.
/// </summary>
/// <remarks>
///     There is no in-memory buffer: instance events are persisted append-only with a monotonic sequence and
///     <see cref="IExternalAppService.ListEventsAsync" /> IS the replay authority — literally the same member the
///     events endpoint pages, so a subscription and the History tab cannot show two different pasts. Nor does a
///     disconnect cancel anything: an install outlives the browser tab that started it.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class ExternalAppHub : Hub
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

    private readonly IExternalAppService _apps;
    private readonly ExternalAppsOptions _options;

    public ExternalAppHub(IExternalAppService apps, IOptions<ExternalAppsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

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
            detail = await _apps.GetAsync(instanceId, cancellationToken);
        }
        catch (ExternalAppNotFoundException)
        {
            throw new HubException(InstanceNotFoundMessage);
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between read and
        // join reaches nobody. The overlap is harmless — every push is an idempotent notification keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), cancellationToken);

        try
        {
            var (events, truncated) = await ReplayWindow.ReadAsync(ReplayCap, limit => _apps.ListEventsAsync(instanceId, afterSequence, limit, cancellationToken));

            return new ExternalAppSubscriptionSnapshot
            {
                InstanceId = instanceId,
                Status = detail.Summary.Status.ToString(),
                DesiredState = detail.Summary.DesiredState.ToString(),
                FailureCategory = detail.Summary.FailureCategory?.ToString(),
                LastSequence = detail.LastSequence,
                Events = [.. events.Select(ExternalAppMapper.ToEventView)],
                ReplayTruncated = truncated
            };
        }
        catch (ExternalAppNotFoundException)
        {
            // The instance was deleted between the two reads. The caller is told what it would have been told had the
            // first read noticed — a generic hub failure would make a routine race look like a node fault.
            await LeaveAfterFailedSubscribeAsync(instanceId);
            throw new HubException(InstanceNotFoundMessage);
        }
        catch
        {
            await LeaveAfterFailedSubscribeAsync(instanceId);
            throw;
        }
    }

    public Task Unsubscribe(Guid instanceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), Context.ConnectionAborted);

    /// <summary>
    ///     Leaves the instance group again after a subscribe that threw.
    /// </summary>
    /// <remarks>
    ///     A subscribe that threw hands its caller no watermark and no handle to unsubscribe with, so a membership left
    ///     behind would push this instance's changes at a connection that never received its replay. Rolled back with
    ///     <see cref="CancellationToken.None" />: the failure being an aborted connection is exactly the case where the
    ///     rollback must still run rather than inherit the cancellation and mask the original exception.
    /// </remarks>
    private Task LeaveAfterFailedSubscribeAsync(Guid instanceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ExternalAppHubGroups.Instance(instanceId), CancellationToken.None);
}
