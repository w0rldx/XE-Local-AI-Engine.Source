namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

public static class WorkSessionHubEvents
{
    public const string Changed = "workSessionChanged";
}

/// <summary>
///     What changed and where the store now stands. <see cref="Kind" /> is lowercase on the wire — the client switches
///     on the literal — and the payload deliberately carries no content: the subscriber re-reads the named feed from
///     its own watermark, so a dropped push degrades to a late read rather than to a wrong render.
/// </summary>
public sealed record WorkSessionChanged(Guid SessionId, long Seq, string Kind);

public sealed record WorkSessionSubscriptionSnapshot(Guid SessionId,
    string Status,
    int Step,
    Guid? CurrentTaskId,
    long LastSeq,
    IReadOnlyList<WorkSessionEventResponse> Events,
    bool ReplayTruncated);

/// <summary>
///     Operator-only live notifications for one work session.
///     <para>
///         There is no in-memory event buffer behind this hub, unlike the four transient-output hubs beside it: work
///         session events are persisted append-only with a monotonic sequence, so the store IS the replay authority and
///         a buffer would only be a cache in front of it that can fall out of step. A client absent for a day replays
///         correctly by passing the highest sequence it has seen.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class WorkSessionHub(IWorkSessionService service, IOptions<WorkSessionOptions> options) : Hub
{
    /// <summary>
    ///     How many persisted events one subscribe hands back. Past this the snapshot says so and the client pages the
    ///     event feed by <c>sinceSeq</c> — one extra round trip on a session left running for a long time, against an
    ///     unbounded first frame for every subscriber.
    /// </summary>
    private const int ReplayCap = 200;

    private readonly WorkSessionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task<WorkSessionSubscriptionSnapshot> SubscribeSession(Guid sessionId, long afterSeq)
    {
        if (!_options.Enabled)
        {
            throw new HubException("Work sessions are disabled on this node.");
        }

        if (sessionId == Guid.Empty)
        {
            throw new HubException("Work session id is required.");
        }

        if (afterSeq < 0)
        {
            throw new HubException("Work session replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        WorkSessionDetail session;
        try
        {
            session = await _service.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw new HubException("Work session was not found.");
        }

        // Join BEFORE reading the replay: the other order leaves a window in which a change published between the read
        // and the join reaches nobody. The overlap this creates is harmless — every push is an idempotent notification
        // keyed by sequence.
        await Groups.AddToGroupAsync(Context.ConnectionId, WorkSessionHubGroups.Session(sessionId), cancellationToken).ConfigureAwait(false);

        // One over the cap, so "there is more" is observed rather than inferred from a full page.
        var events = await _service.ListEventsAsync(sessionId, afterSeq, ReplayCap + 1, cancellationToken).ConfigureAwait(false);
        var truncated = events.Count > ReplayCap;
        return new WorkSessionSubscriptionSnapshot(sessionId,
            session.Status.ToString(),
            session.StepCount,
            session.CurrentTaskId,
            session.LastSequence,
            [.. events.Take(ReplayCap).Select(WorkSessionContractMapper.ToResponse)],
            truncated);
    }

    public Task UnsubscribeSession(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkSessionHubGroups.Session(sessionId));
}

internal static class WorkSessionHubGroups
{
    public static string Session(Guid sessionId) =>
        string.Concat("work-session-", sessionId.ToString("N"));
}
